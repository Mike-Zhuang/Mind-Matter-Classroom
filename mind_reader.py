import cv2
import mediapipe as mp
import time
import math
import socket
import numpy as np
import base64
import requests
import json

# --- 1. 智谱 AI 配置 (Key已就位) ---
ZHIPU_API_KEY = "4633fe0c06c44b1ea80d3fd2febc800c.pJlOSVyHs3D33jsD"
ZHIPU_API_URL = "https://open.bigmodel.cn/api/paas/v4/chat/completions"

def analyze_book_with_zhipu(image):
    """
    发送图片给智谱 GLM-4V，识别学科
    """
    print("🤖 正在请求智谱 AI (GLM-4V)...")
    _, buffer = cv2.imencode('.jpg', image)
    img_str = base64.b64encode(buffer).decode('utf-8')
    headers = { "Authorization": f"Bearer {ZHIPU_API_KEY}", "Content-Type": "application/json" }
    prompt = "你是一个底层的分类API。请分析图片内容，判断它属于哪个学科。不要解释，不要标点，不要废话。请严格直接返回以下单词之一：Physics, Math, History, Geography。如果无法识别，返回 None。"
    data = {
        "model": "glm-4v",
        "messages": [
            { "role": "user", "content": [ { "type": "text", "text": prompt }, { "type": "image_url", "image_url": { "url": img_str } } ] }
        ],
        "temperature": 0.1
    }
    try:
        response = requests.post(ZHIPU_API_URL, headers=headers, json=data)
        if response.status_code == 200:
            result = response.json()
            content = result['choices'][0]['message']['content'].strip()
            print(f"🤖 AI 原始回复: [{content}]")
            content_lower = content.lower()
            if "physics" in content_lower or "物理" in content_lower: return "Physics"
            elif "math" in content_lower or "数学" in content_lower: return "Math"
            elif "history" in content_lower or "历史" in content_lower: return "History"
            elif "geography" in content_lower or "地理" in content_lower: return "Geography"
            else: return "None"
        else: return None
    except Exception as e:
        print(f"❌ 请求异常: {e}")
        return None

# --- 2. 网络通信设置 ---
UDP_IP = "127.0.0.1"
UDP_PORT = 5005
sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

def send_to_unity(message):
    sock.sendto(message.encode(), (UDP_IP, UDP_PORT))

# --- 3. MediaPipe 初始化 (Face + Hands) ---
mp_face_mesh = mp.solutions.face_mesh
face_mesh = mp_face_mesh.FaceMesh(max_num_faces=1, refine_landmarks=True, min_detection_confidence=0.5, min_tracking_confidence=0.5)

# [核心升级] 开启双抓取
mp_hands = mp.solutions.hands
hands_tracker = mp_hands.Hands(
    max_num_hands=2, # 双手模式
    min_detection_confidence=0.7,
    min_tracking_confidence=0.5
)

# --- 辅助函数 ---
def calculate_pixel_distance(p1, p2, w, h):
    x1, y1 = p1.x * w, p1.y * h
    x2, y2 = p2.x * w, p2.y * h
    return math.hypot(x1 - x2, y1 - y2)

def get_head_roll(landmarks, img_w, img_h):
    left_cheek = landmarks[234]
    right_cheek = landmarks[454]
    dy = (right_cheek.y - left_cheek.y) * img_h
    dx = (right_cheek.x - left_cheek.x) * img_w
    return math.degrees(math.atan2(dy, dx))

# --- 变量初始化 ---
calibration_data = []
CALIBRATION_FRAMES = 30 
is_calibrated = False
baseline_brow = 0.0
baseline_ear = 0.0
baseline_roll = 0.0
baseline_mar = 0.0 
baseline_smile_ratio = 0.0 
baseline_face_scale = 0.0 

CONFUSED_BROW_TRIGGER = 0.0
SLEEP_EAR_TRIGGER = 0.0
SQUINT_TRIGGER = 0.0 
YAWN_TRIGGER = 0.0 
BASE_SMILE_ENTER = 0.0 
BASE_SMILE_EXIT = 0.0  
smile_active_raw = False 
DEPTH_COMPENSATION_FACTOR = 0.15 
TILT_TRIGGER_ANGLE = 12.0  

fatigue_level = 0.0
FATIGUE_MAX = 100.0
FATIGUE_THRESHOLD = 80.0
FATIGUE_INCREASE_EYE = 3.0   
FATIGUE_INCREASE_YAWN = 1.5  
FATIGUE_DECREASE = 0.5       
FATIGUE_INC_NORMAL = 3.0
FATIGUE_INC_WITH_SMILE = 1.0 
FATIGUE_INC_YAWN = 1.5
FATIGUE_DEC_NORMAL = 0.5
FATIGUE_DEC_SMILE = 1.0

eyes_closed_frame_counter = 0
BLINK_FILTER_FRAMES = 8      

confusion_level = 0.0
CONFUSION_MAX = 100.0
CONFUSION_THRESHOLD = 50.0 
CONFUSION_INC = 1.5         
CONFUSION_DEC = 3.0        

smile_level = 0.0
SMILE_MAX = 100.0
SMILE_THRESHOLD = 40.0
SMILE_INC = 2.0             
SMILE_DEC = 10.0            

DEBUG_POINT_INDICES = [33, 133, 159, 145, 362, 263, 386, 374, 336, 107, 234, 454, 13, 14, 61, 291]
last_ai_result = "None"
is_analyzing = False
enable_hand_tracking = True # 手势总开关

# --- 主程序 ---
cap = cv2.VideoCapture(0)

print("启动... [C]校准 [B]识别书本 [H]手势开关")

while cap.isOpened():
    success, image = cap.read()
    if not success: continue

    image = cv2.flip(image, 1)
    h, w, c = image.shape
    rgb_image = cv2.cvtColor(image, cv2.COLOR_BGR2RGB)
    
    key = cv2.waitKey(5) & 0xFF
    if key == ord('q'): break
    elif key == ord('c'):
        is_calibrated = False
        calibration_data = []
        smile_active_raw = False 
        print("重置校准...")
    elif key == ord('h'):
        enable_hand_tracking = not enable_hand_tracking
        print(f"手势控制: {enable_hand_tracking}")
    elif key == ord('b') and not is_analyzing:
        is_analyzing = True
        cv2.putText(image, "Scanning Book...", (w//2-150, h//2), cv2.FONT_HERSHEY_SIMPLEX, 1.5, (0, 0, 255), 3)
        cv2.imshow('Future Classroom - Dual Bars', image)
        cv2.waitKey(1) 
        subject = analyze_book_with_zhipu(image)
        if subject in ['Physics', 'Math', 'History', 'Geography']:
            send_to_unity(f"SUB:{subject}")
            last_ai_result = subject
        else:
            last_ai_result = "Unknown"
        is_analyzing = False

    # 1. Face Mesh
    results_face = face_mesh.process(rgb_image)
    current_state = "Wait for Calib..."
    color = (200, 200, 200)

    if results_face.multi_face_landmarks:
        for face_landmarks in results_face.multi_face_landmarks:
            lm = face_landmarks.landmark
            for idx in DEBUG_POINT_INDICES:
                pt = lm[idx]
                cv2.circle(image, (int(pt.x * w), int(pt.y * h)), 2, (0, 255, 0), -1)

            left_eye_v = calculate_pixel_distance(lm[159], lm[145], w, h)
            left_eye_h = calculate_pixel_distance(lm[33], lm[133], w, h)
            right_eye_v = calculate_pixel_distance(lm[386], lm[374], w, h)
            right_eye_h = calculate_pixel_distance(lm[362], lm[263], w, h)
            if left_eye_h == 0: left_eye_h = 1
            if right_eye_h == 0: right_eye_h = 1
            avg_ear = ((left_eye_v / left_eye_h) + (right_eye_v / right_eye_h)) / 2.0
            inner_eye_dist = calculate_pixel_distance(lm[133], lm[362], w, h)
            if inner_eye_dist == 0: inner_eye_dist = 1
            current_face_scale = inner_eye_dist 
            brow_dist = calculate_pixel_distance(lm[336], lm[107], w, h)
            brow_ratio = brow_dist / inner_eye_dist
            mouth_v = calculate_pixel_distance(lm[13], lm[14], w, h)
            mouth_h = calculate_pixel_distance(lm[61], lm[291], w, h) 
            current_mar = mouth_v / mouth_h
            current_smile_ratio = mouth_h / inner_eye_dist
            roll = get_head_roll(lm, w, h)

            if not is_calibrated:
                current_state = "CALIBRATING..."
                calibration_data.append([avg_ear, brow_ratio, roll, current_mar, current_smile_ratio, current_face_scale])
                progress = len(calibration_data) / CALIBRATION_FRAMES
                cv2.rectangle(image, (50, 200), (int(50 + 300 * progress), 230), (0, 255, 0), -1)
                cv2.putText(image, "RELAX NO SMILE", (50, 100), cv2.FONT_HERSHEY_SIMPLEX, 0.8, (0,255,255), 2)
                if len(calibration_data) >= CALIBRATION_FRAMES:
                    data = np.array(calibration_data)
                    baseline_ear = np.mean(data[:, 0])
                    baseline_brow = np.mean(data[:, 1])
                    baseline_roll = np.mean(data[:, 2])
                    baseline_mar = np.mean(data[:, 3])
                    baseline_smile_ratio = np.mean(data[:, 4])
                    baseline_face_scale = np.mean(data[:, 5])
                    SLEEP_EAR_TRIGGER = baseline_ear * 0.70
                    CONFUSED_BROW_TRIGGER = baseline_brow * 0.98 
                    SQUINT_TRIGGER = baseline_ear * 0.90
                    YAWN_TRIGGER = baseline_mar + 0.4
                    BASE_SMILE_ENTER = baseline_smile_ratio * 1.08
                    BASE_SMILE_EXIT = baseline_smile_ratio * 1.04
                    is_calibrated = True
            else:
                is_eyes_fully_closed = avg_ear < SLEEP_EAR_TRIGGER
                is_yawning = current_mar > YAWN_TRIGGER
                scale_factor = current_face_scale / baseline_face_scale
                depth_compensation = 1.0 + (scale_factor - 1.0) * DEPTH_COMPENSATION_FACTOR
                adaptive_enter_thresh = BASE_SMILE_ENTER * depth_compensation
                adaptive_exit_thresh = BASE_SMILE_EXIT * depth_compensation

                if not smile_active_raw:
                    if current_smile_ratio > adaptive_enter_thresh: smile_active_raw = True
                else:
                    if current_smile_ratio < adaptive_exit_thresh: smile_active_raw = False
                
                if smile_active_raw: smile_level += SMILE_INC
                else: smile_level -= SMILE_DEC
                smile_level = max(0.0, min(smile_level, SMILE_MAX))
                is_happy_confirmed = smile_level > SMILE_THRESHOLD
                cond_frown = brow_ratio < CONFUSED_BROW_TRIGGER
                is_squinting = (avg_ear < SQUINT_TRIGGER) and (avg_ear > SLEEP_EAR_TRIGGER)
                is_slight_tension = brow_ratio < (baseline_brow * 0.995) 
                cond_focus_look = is_squinting and is_slight_tension
                is_confused_gesture_raw = cond_frown or cond_focus_look
                diff_roll = abs(roll - baseline_roll)
                is_tilting_raw = diff_roll > TILT_TRIGGER_ANGLE

                if is_happy_confirmed:
                    is_confused_gesture_raw = False
                    is_tilting_raw = False
                if (is_confused_gesture_raw or is_tilting_raw) and not is_eyes_fully_closed:
                    confusion_level += CONFUSION_INC
                else:
                    confusion_level -= CONFUSION_DEC
                confusion_level = max(0.0, min(confusion_level, CONFUSION_MAX))
                is_confused_confirmed = confusion_level > CONFUSION_THRESHOLD
                if is_eyes_fully_closed: eyes_closed_frame_counter += 1
                else: eyes_closed_frame_counter = 0
                is_real_sleep = eyes_closed_frame_counter > BLINK_FILTER_FRAMES

                status_detail = ""
                if is_yawning:
                    fatigue_level += FATIGUE_INC_YAWN
                    status_detail = "Yawn"
                elif is_real_sleep:
                    if is_happy_confirmed: fatigue_level += FATIGUE_INC_WITH_SMILE; status_detail = "Smiling Doze"
                    else: fatigue_level += FATIGUE_INC_NORMAL; status_detail = "Sleep"
                else:
                    if is_happy_confirmed: fatigue_level -= FATIGUE_DEC_SMILE; status_detail = "Smiling"
                    elif is_confused_confirmed: fatigue_level -= FATIGUE_DEC_NORMAL; status_detail = "Think"
                    else: fatigue_level -= FATIGUE_DEC_NORMAL; status_detail = "Monitor"
                
                fatigue_level = max(0.0, min(fatigue_level, FATIGUE_MAX))
                if fatigue_level > FATIGUE_THRESHOLD: current_state = "SLEEPY"; color = (0, 0, 255) 
                elif is_confused_confirmed and not is_happy_confirmed: current_state = "CONFUSED"; color = (0, 255, 255) 
                elif is_happy_confirmed: current_state = "HAPPY"; color = (0, 255, 0)
                else: current_state = "NORMAL"; color = (0, 255, 0)

                send_to_unity(current_state)

                # UI
                cv2.putText(image, f"STATUS: {current_state}", (30, 50), cv2.FONT_HERSHEY_SIMPLEX, 1, color, 3)
                cv2.putText(image, f"AI Subject: {last_ai_result}", (30, 100), cv2.FONT_HERSHEY_SIMPLEX, 1, (255, 0, 255), 2)
                cv2.putText(image, status_detail, (w//2 + 110, h-25), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (255,255,255), 1)

                bar_len_fatigue = int((fatigue_level / FATIGUE_MAX) * 200)
                cv2.rectangle(image, (w//2 - 100, h-40), (w//2 - 100 + bar_len_fatigue, h-20), (0, 0, 255), -1)
                cv2.rectangle(image, (w//2 - 100, h-40), (w//2 + 100, h-20), (255, 255, 255), 2)
                
                bar_len_conf = int((confusion_level / CONFUSION_MAX) * 150)
                if confusion_level > 0:
                    cv2.rectangle(image, (w-40, h-50), (w-20, h-50 - bar_len_conf), (0, 255, 255), -1)
                    cv2.rectangle(image, (w-40, h-50), (w-20, h-200), (255, 255, 255), 1)
                    cv2.putText(image, "Conf", (w-55, h-30), cv2.FONT_HERSHEY_SIMPLEX, 0.4, (0,255,255), 1)

                bar_len_smile = int((smile_level / SMILE_MAX) * 150)
                if smile_level > 0:
                    cv2.rectangle(image, (20, h-50), (40, h-50 - bar_len_smile), (0, 255, 0), -1)
                    cv2.rectangle(image, (20, h-50), (40, h-200), (255, 255, 255), 1)
                    cv2.putText(image, "Joy", (15, h-30), cv2.FONT_HERSHEY_SIMPLEX, 0.4, (0,255,0), 1)

    # 2. [双极手势追踪]
    if enable_hand_tracking:
        results_hands = hands_tracker.process(rgb_image)
        # 默认发两个 NONE，除非检测到
        hand_msg_l = "HAND_L:NONE"
        hand_msg_r = "HAND_R:NONE"
        
        if results_hands.multi_hand_landmarks and results_hands.multi_handedness:
            for idx, hand_handedness in enumerate(results_hands.multi_handedness):
                hand_landmarks = results_hands.multi_hand_landmarks[idx]
                
                # 获取食指指尖 (8)
                index_tip = hand_landmarks.landmark[8]
                cx, cy = int(index_tip.x * w), int(index_tip.y * h)
                
                # 判断左右手 (MediaPipe的Left/Right通常是反的，需要测试)
                # 这里我们假设 Label "Left" 就是屏幕上的左手（其实是用户的右手）
                # 为了简单，我们直接用 Label 来区分颜色
                label = hand_handedness.classification[0].label
                
                if label == "Left": 
                    # 红色圈 = 左手 = 隆起
                    cv2.circle(image, (cx, cy), 15, (0, 0, 255), -1)
                    cv2.putText(image, "L", (cx-10, cy+5), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (255,255,255), 1)
                    hand_msg_l = f"HAND_L:{index_tip.x},{index_tip.y}"
                else: 
                    # 蓝色圈 = 右手 = 黑洞
                    cv2.circle(image, (cx, cy), 15, (255, 0, 0), -1)
                    cv2.putText(image, "R", (cx-10, cy+5), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (255,255,255), 1)
                    hand_msg_r = f"HAND_R:{index_tip.x},{index_tip.y}"

        send_to_unity(hand_msg_l)
        send_to_unity(hand_msg_r)

    status_text = "Hand: ON" if enable_hand_tracking else "Hand: OFF"
    cv2.putText(image, status_text, (w - 150, h - 20), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (255, 255, 0), 2)
    cv2.imshow('Future Classroom - Dual Bars', image)

cap.release()
cv2.destroyAllWindows()
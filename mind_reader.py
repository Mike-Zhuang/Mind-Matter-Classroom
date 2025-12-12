import cv2
import mediapipe as mp
import time
import math
import socket
import numpy as np

# --- 1. 网络通信设置 ---
UDP_IP = "127.0.0.1"
UDP_PORT = 5005
sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

def send_to_unity(message):
    sock.sendto(message.encode(), (UDP_IP, UDP_PORT))

# --- 2. MediaPipe 初始化 ---
mp_face_mesh = mp.solutions.face_mesh
face_mesh = mp_face_mesh.FaceMesh(
    max_num_faces=1,
    refine_landmarks=True,
    min_detection_confidence=0.5,
    min_tracking_confidence=0.5
)

def calculate_pixel_distance(p1, p2, w, h):
    x1, y1 = p1.x * w, p1.y * h
    x2, y2 = p2.x * w, p2.y * h
    return math.hypot(x1 - x2, y1 - y2)

def get_head_roll(landmarks, img_w, img_h):
    left_cheek = landmarks[234]
    right_cheek = landmarks[454]
    dy = (right_cheek.y - left_cheek.y) * img_h
    dx = (right_cheek.x - left_cheek.x) * img_w
    angle = math.degrees(math.atan2(dy, dx))
    return angle

# --- 3. 变量初始化 ---
calibration_data = []
CALIBRATION_FRAMES = 30 
is_calibrated = False

baseline_brow = 0.0
baseline_ear = 0.0
baseline_roll = 0.0
baseline_mar = 0.0 
baseline_smile_ratio = 0.0 
baseline_face_scale = 0.0 # 新增：记录校准时的脸部大小(眼距)

CONFUSED_BROW_TRIGGER = 0.0
SLEEP_EAR_TRIGGER = 0.0
SQUINT_TRIGGER = 0.0 
YAWN_TRIGGER = 0.0 

# 基础阈值
BASE_SMILE_ENTER = 0.0 
BASE_SMILE_EXIT = 0.0  
smile_active = False     

# *** 核心参数：深度补偿系数 ***
# 当脸变大时，每变大1倍，微笑门槛额外增加多少？
# 0.15 意味着：如果脸变大1倍，门槛比值自动+15% (抵消你的110->130涨幅)
DEPTH_COMPENSATION_FACTOR = 0.15 

TILT_TRIGGER_ANGLE = 12.0  

# 疲劳策略
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

confused_frame_counter = 0
CONFUSED_CONFIRM_FRAMES = 5 

DEBUG_POINT_INDICES = [33, 133, 159, 145, 362, 263, 386, 374, 336, 107, 234, 454, 13, 14, 61, 291]

# --- 4. 主程序 ---
cap = cv2.VideoCapture(0)

print("启动... 请在正常距离完全放松面部，按 'c' 键校准。")

while cap.isOpened():
    success, image = cap.read()
    if not success:
        continue

    image = cv2.flip(image, 1)
    h, w, c = image.shape
    rgb_image = cv2.cvtColor(image, cv2.COLOR_BGR2RGB)
    results = face_mesh.process(rgb_image)

    current_state = "Wait for Calib..."
    color = (200, 200, 200)
    
    key = cv2.waitKey(5) & 0xFF
    if key == ord('q'):
        break
    elif key == ord('c'):
        is_calibrated = False
        calibration_data = []
        smile_active = False 
        print("重置校准...")

    if results.multi_face_landmarks:
        for face_landmarks in results.multi_face_landmarks:
            lm = face_landmarks.landmark

            for idx in DEBUG_POINT_INDICES:
                pt = lm[idx]
                cv2.circle(image, (int(pt.x * w), int(pt.y * h)), 3, (0, 255, 0), -1)

            # --- 数据计算 ---
            left_eye_v = calculate_pixel_distance(lm[159], lm[145], w, h)
            left_eye_h = calculate_pixel_distance(lm[33], lm[133], w, h)
            right_eye_v = calculate_pixel_distance(lm[386], lm[374], w, h)
            right_eye_h = calculate_pixel_distance(lm[362], lm[263], w, h)
            if left_eye_h == 0: left_eye_h = 1
            if right_eye_h == 0: right_eye_h = 1
            avg_ear = ((left_eye_v / left_eye_h) + (right_eye_v / right_eye_h)) / 2.0

            # 这里的 face_scale 用内眼角间距 (最稳定的骨骼距离)
            inner_eye_dist = calculate_pixel_distance(lm[133], lm[362], w, h)
            if inner_eye_dist == 0: inner_eye_dist = 1
            current_face_scale = inner_eye_dist # 当前脸有多大

            brow_dist = calculate_pixel_distance(lm[336], lm[107], w, h)
            brow_ratio = brow_dist / inner_eye_dist

            mouth_v = calculate_pixel_distance(lm[13], lm[14], w, h)
            mouth_h = calculate_pixel_distance(lm[61], lm[291], w, h) 
            current_mar = mouth_v / mouth_h

            current_smile_ratio = mouth_h / inner_eye_dist

            roll = get_head_roll(lm, w, h)

            # --- 校准 ---
            if not is_calibrated:
                current_state = "CALIBRATING..."
                # 记录当前的脸部大小
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
                    
                    # 设定【基础】微笑阈值 (针对正常距离)
                    BASE_SMILE_ENTER = baseline_smile_ratio * 1.08
                    BASE_SMILE_EXIT = baseline_smile_ratio * 1.04
                    
                    is_calibrated = True
                    print(f"校准完成! BaseScale:{int(baseline_face_scale)}")

            # --- 检测 ---
            else:
                is_eyes_fully_closed = avg_ear < SLEEP_EAR_TRIGGER
                is_yawning = current_mar > YAWN_TRIGGER
                
                # *** 核心修改：动态计算微笑阈值 ***
                # 1. 计算当前变焦倍率 (Scale Factor)
                # 比如：当前 200px / 基准 100px = 2.0 (脸变大2倍)
                scale_factor = current_face_scale / baseline_face_scale
                
                # 2. 计算深度补偿
                # 脸越大，门槛越高。如果 scale_factor = 2.0，门槛增加 15%
                depth_compensation = 1.0 + (scale_factor - 1.0) * DEPTH_COMPENSATION_FACTOR
                
                # 3. 应用补偿
                # 注意：如果往后退(scale<1)，补偿系数<1，门槛自动降低，防止离远了笑不出来
                adaptive_enter_thresh = BASE_SMILE_ENTER * depth_compensation
                adaptive_exit_thresh = BASE_SMILE_EXIT * depth_compensation

                # 4. 微笑判定
                if not smile_active:
                    if current_smile_ratio > adaptive_enter_thresh: smile_active = True
                else:
                    if current_smile_ratio < adaptive_exit_thresh: smile_active = False
                
                # 困惑特征
                cond_frown = brow_ratio < CONFUSED_BROW_TRIGGER
                is_squinting = (avg_ear < SQUINT_TRIGGER) and (avg_ear > SLEEP_EAR_TRIGGER)
                is_slight_tension = brow_ratio < (baseline_brow * 0.995) 
                cond_focus_look = is_squinting and is_slight_tension
                
                is_confused_gesture_raw = cond_frown or cond_focus_look
                diff_roll = abs(roll - baseline_roll)
                is_tilting_raw = diff_roll > TILT_TRIGGER_ANGLE

                # 微笑优先过滤
                if smile_active:
                    is_confused_gesture_raw = False
                    is_tilting_raw = False

                if is_confused_gesture_raw or is_tilting_raw:
                    confused_frame_counter += 1
                else:
                    confused_frame_counter = 0
                is_confused_confirmed = confused_frame_counter >= CONFUSED_CONFIRM_FRAMES

                # 眨眼过滤
                if is_eyes_fully_closed:
                    eyes_closed_frame_counter += 1
                else:
                    eyes_closed_frame_counter = 0
                is_real_sleep = eyes_closed_frame_counter > BLINK_FILTER_FRAMES

                status_detail = ""

                # 平衡逻辑
                if is_yawning:
                    fatigue_level += FATIGUE_INC_YAWN
                    status_detail = "Yawning (Fatigue+)"
                elif is_real_sleep:
                    if smile_active:
                        fatigue_level += FATIGUE_INC_WITH_SMILE 
                        status_detail = "Smiling Doze"
                    else:
                        fatigue_level += FATIGUE_INC_NORMAL
                        status_detail = "Sleeping"
                else:
                    if smile_active:
                        fatigue_level -= FATIGUE_DEC_SMILE
                        status_detail = "Smiling :)"
                    elif is_confused_confirmed:
                        fatigue_level -= FATIGUE_DEC_NORMAL
                        status_detail = "Thinking"
                    else:
                        fatigue_level -= FATIGUE_DEC_NORMAL
                        if eyes_closed_frame_counter > 0:
                            status_detail = "Blinking"
                        else:
                            status_detail = "Monitoring"
                
                fatigue_level = max(0.0, min(fatigue_level, FATIGUE_MAX))

                if fatigue_level > FATIGUE_THRESHOLD:
                    current_state = "SLEEPY"
                    color = (0, 0, 255) 
                elif is_confused_confirmed and not smile_active:
                    current_state = "CONFUSED"
                    color = (0, 255, 255) 
                elif smile_active:
                    current_state = "NORMAL"
                    color = (0, 255, 0)
                else:
                    current_state = "NORMAL"
                    color = (0, 255, 0)

                send_to_unity(current_state)

                # --- 界面可视化 ---
                cv2.putText(image, f"STATUS: {current_state}", (30, 50), cv2.FONT_HERSHEY_SIMPLEX, 1, color, 3)

                # 微笑数值 (显示自适应门槛)
                c_smile = (0, 255, 0) if smile_active else (200, 200, 200)
                curr_disp = int(current_smile_ratio * 100)
                
                # 这里显示的 Trig 是会动态变化的！
                thresh_disp = int((adaptive_exit_thresh if smile_active else adaptive_enter_thresh) * 100)
                thresh_label = "Exit" if smile_active else "Enter"
                
                cv2.putText(image, f"SmileRatio: {curr_disp} ({thresh_label} > {thresh_disp})", (w-350, 40), cv2.FONT_HERSHEY_SIMPLEX, 0.6, c_smile, 2)
                
                # 也可以显示一个 Scale 倍率，让你看到补偿有没有生效
                cv2.putText(image, f"Zoom: {scale_factor:.2f}x", (w-350, 20), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (150,150,150), 1)

                c_brow = (0, 0, 255) if is_confused_gesture_raw else (200, 200, 200)
                cv2.putText(image, f"Brow: {brow_ratio*100:.1f} (Trig < {CONFUSED_BROW_TRIGGER*100:.1f})", (w-350, 70), cv2.FONT_HERSHEY_SIMPLEX, 0.6, c_brow, 2)

                bar_len = int((fatigue_level / FATIGUE_MAX) * 200)
                cv2.rectangle(image, (w//2 - 100, h-40), (w//2 - 100 + bar_len, h-20), (0, 0, 255), -1)
                cv2.rectangle(image, (w//2 - 100, h-40), (w//2 + 100, h-20), (255, 255, 255), 2)
                cv2.putText(image, status_detail, (w//2 + 110, h-25), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (255,255,255), 1)

    
    cv2.imshow('Future Classroom - Adaptive Depth', image)

cap.release()
cv2.destroyAllWindows()
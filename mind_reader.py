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
baseline_face_scale = 0.0 

CONFUSED_BROW_TRIGGER = 0.0
SLEEP_EAR_TRIGGER = 0.0
SQUINT_TRIGGER = 0.0 
YAWN_TRIGGER = 0.0 

BASE_SMILE_ENTER = 0.0 
BASE_SMILE_EXIT = 0.0  
smile_active = False     

DEPTH_COMPENSATION_FACTOR = 0.15 
TILT_TRIGGER_ANGLE = 12.0  

# --- 疲劳策略 ---
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

# --- 新增：困惑进度条策略 ---
confusion_level = 0.0
CONFUSION_MAX = 100.0
CONFUSION_THRESHOLD = 50.0  # 超过一半才触发
CONFUSION_INC = 8.0         # 涨得比较快 (约0.3秒触发)
CONFUSION_DEC = 10.0        # 降得更快 (一放松马上消失)

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

            # 数据计算
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

            # 校准
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
                    print(f"校准完成! BaseScale:{int(baseline_face_scale)}")

            # 检测
            else:
                is_eyes_fully_closed = avg_ear < SLEEP_EAR_TRIGGER
                is_yawning = current_mar > YAWN_TRIGGER
                
                # 微笑判定 (深度补偿)
                scale_factor = current_face_scale / baseline_face_scale
                depth_compensation = 1.0 + (scale_factor - 1.0) * DEPTH_COMPENSATION_FACTOR
                adaptive_enter_thresh = BASE_SMILE_ENTER * depth_compensation
                adaptive_exit_thresh = BASE_SMILE_EXIT * depth_compensation

                if not smile_active:
                    if current_smile_ratio > adaptive_enter_thresh: smile_active = True
                else:
                    if current_smile_ratio < adaptive_exit_thresh: smile_active = False
                
                # 困惑特征检测 (Raw Features)
                cond_frown = brow_ratio < CONFUSED_BROW_TRIGGER
                is_squinting = (avg_ear < SQUINT_TRIGGER) and (avg_ear > SLEEP_EAR_TRIGGER)
                is_slight_tension = brow_ratio < (baseline_brow * 0.995) 
                cond_focus_look = is_squinting and is_slight_tension
                
                is_confused_gesture_raw = cond_frown or cond_focus_look
                diff_roll = abs(roll - baseline_roll)
                is_tilting_raw = diff_roll > TILT_TRIGGER_ANGLE

                # 微笑时，强制不困惑
                if smile_active:
                    is_confused_gesture_raw = False
                    is_tilting_raw = False

                # *** 核心修改：困惑进度条逻辑 ***
                if is_confused_gesture_raw or is_tilting_raw:
                    confusion_level += CONFUSION_INC
                else:
                    confusion_level -= CONFUSION_DEC
                
                confusion_level = max(0.0, min(confusion_level, CONFUSION_MAX))
                
                # 只有进度条超过阈值，才算真正的 CONFUSED
                is_confused_confirmed = confusion_level > CONFUSION_THRESHOLD

                # 眨眼过滤
                if is_eyes_fully_closed:
                    eyes_closed_frame_counter += 1
                else:
                    eyes_closed_frame_counter = 0
                is_real_sleep = eyes_closed_frame_counter > BLINK_FILTER_FRAMES

                status_detail = ""

                # 疲劳/状态逻辑
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

                # 最终状态输出
                if fatigue_level > FATIGUE_THRESHOLD:
                    current_state = "SLEEPY"
                    color = (0, 0, 255) 
                elif is_confused_confirmed and not smile_active:
                    current_state = "CONFUSED"
                    color = (0, 255, 255) 
                elif smile_active:
                    current_state = "HAPPY"
                    color = (0, 255, 0)
                else:
                    current_state = "NORMAL"
                    color = (0, 255, 0)

                send_to_unity(current_state)

                # --- 界面可视化 ---
                cv2.putText(image, f"STATUS: {current_state}", (30, 50), cv2.FONT_HERSHEY_SIMPLEX, 1, color, 3)

                # 微笑数据
                c_smile = (0, 255, 0) if smile_active else (200, 200, 200)
                curr_disp = int(current_smile_ratio * 100)
                thresh_disp = int((adaptive_exit_thresh if smile_active else adaptive_enter_thresh) * 100)
                cv2.putText(image, f"SmileRatio: {curr_disp} (>{thresh_disp})", (w-350, 40), cv2.FONT_HERSHEY_SIMPLEX, 0.6, c_smile, 2)
                
                # 困惑原始数据
                c_brow = (0, 0, 255) if is_confused_gesture_raw else (200, 200, 200)
                cv2.putText(image, f"Brow: {brow_ratio*100:.1f}", (w-350, 70), cv2.FONT_HERSHEY_SIMPLEX, 0.6, c_brow, 2)

                # --- 绘制两个进度条 ---
                # 1. 红色：疲劳条 (Fatigue)
                bar_len_fatigue = int((fatigue_level / FATIGUE_MAX) * 200)
                cv2.rectangle(image, (w//2 - 100, h-40), (w//2 - 100 + bar_len_fatigue, h-20), (0, 0, 255), -1)
                cv2.rectangle(image, (w//2 - 100, h-40), (w//2 + 100, h-20), (255, 255, 255), 2)
                cv2.putText(image, status_detail, (w//2 + 110, h-25), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (255,255,255), 1)

                # 2. 黄色：困惑条 (Confusion) - 放在右侧
                bar_len_conf = int((confusion_level / CONFUSION_MAX) * 150)
                # 只有有值的时候才画，不占地方
                if confusion_level > 0:
                    cv2.rectangle(image, (w-40, h-50), (w-20, h-50 - bar_len_conf), (0, 255, 255), -1)
                    cv2.rectangle(image, (w-40, h-50), (w-20, h-200), (255, 255, 255), 1)
                    cv2.putText(image, "Conf", (w-55, h-30), cv2.FONT_HERSHEY_SIMPLEX, 0.4, (0,255,255), 1)

    cv2.imshow('Future Classroom - Dual Bars', image)

cap.release()
cv2.destroyAllWindows()
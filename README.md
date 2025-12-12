# Mind-Matter: Affective Swarm Robotics for Future Classroom

# 心物耦合：面向未来教室的情感驱动群体机器人空间

> 同济大学“未来技术导论”课程大作业 | Tongji University Future Technology Coursework

![Python](https://img.shields.io/badge/Python-3.10-blue) ![Unity](https://img.shields.io/badge/Unity-2022.3-black) ![MediaPipe](https://img.shields.io/badge/AI-MediaPipe-orange)

## 📖 项目介绍 (Introduction)

本项目旨在探索未来 10-15 年后的智慧教育场景。通过计算机视觉捕捉学生的情绪状态（困惑、疲劳、兴奋），并实时驱动桌面上的“微型群体机器人”进行物理重构，实现从“图形界面 (GUI)”到“实体界面 (RUI)”的交互跨越。

## 🚀 核心功能 (Features)

* **多模态情感计算 (Python)**:
  * 基于 MediaPipe 的面部关键点检测。
  * 高灵敏度困惑检测（皱眉、凝视、歪头）。
  * 基于“施密特触发器”的微笑检测（防抖动）。
  * 疲劳蓄能槽机制（综合眨眼、闭眼、打哈欠）。
* **群体机器人仿真 (Unity 3D)**:
  * UDP 实时数据通信。
  * 机器人蜂群算法（聚合、离散、震动反馈）。

## 🛠️ 安装与运行 (Installation)

### 1. Python 端 (感知层)

```bash
# 1. 创建环境
conda create -n future_class python=3.10
conda activate future_class

# 2. 安装依赖
pip install opencv-python mediapipe numpy

# 3. 运行
python mind_reader.py
```


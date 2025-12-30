using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

public class SwarmController : MonoBehaviour
{
    public enum Subject { Geography, Math, Physics, History }

    [Header("未来参数")]
    public GameObject robotPrefab;

    [Header("智能铺装")]
    public Transform deskSurface;
    public float density = 0.15f;

    [Header("演示控制面板")]
    public Subject currentSubject = Subject.Physics;
    public bool useManualControl = true;
    public bool enableHandInteraction = true;

    [Header("流体物理参数")]
    [Range(0.9f, 0.999f)] public float fluidDamping = 0.96f; // 阻尼：越小波浪消失越快
    public float waveSpeed = 10.0f; // 波浪传播速度

    // 内部数据
    private List<GameObject> robots = new List<GameObject>();
    private List<Vector3> originalPositions = new List<Vector3>();
    private List<Vector3> targetPositions = new List<Vector3>(); // <--- 补上这一行！！！

    // --- 流体核心变量 ---
    // 我们用两个二维数组来模拟波的传递 (Buffer A 和 Buffer B)
    private float[,] heightBuffer1;
    private float[,] heightBuffer2;
    private bool swapFlag = false; // 用于切换缓冲区

    private GameObject[] allBooks;

    // 自适应变量
    private Vector3 bestSpawnCenter;
    private float maxSafeRadius;
    private float robotHeight;

    // 网络
    Thread receiveThread;
    UdpClient client;
    public int port = 5005;

    private string currentState = "NORMAL";
    private string manualState = "NORMAL";

    // 双手数据
    private Vector2 leftHandPosNorm = Vector2.zero;
    private bool isLeftHandActive = false;
    private Vector2 rightHandPosNorm = Vector2.zero;
    private bool isRightHandActive = false;

    private int rowCount;
    private int colCount;
    private float deskMinX, deskMaxX, deskMinZ, deskMaxZ;
    void Start()
    {
        if (deskSurface == null) { Debug.LogError("❌ 致命错误: 请把 Desk 拖入 Desk Surface 槽位!"); return; }

        if (robotPrefab != null) robotHeight = robotPrefab.transform.localScale.y;
        else robotHeight = 0.08f;

        Bounds deskBounds = deskSurface.GetComponent<Renderer>().bounds;
        float deskTopY = deskBounds.max.y;

        deskMinX = deskBounds.min.x;
        deskMaxX = deskBounds.max.x;
        deskMinZ = deskBounds.min.z;
        deskMaxZ = deskBounds.max.z;

        colCount = Mathf.FloorToInt(deskBounds.size.x / density);
        rowCount = Mathf.FloorToInt(deskBounds.size.z / density);

        // 初始化流体缓冲区
        heightBuffer1 = new float[colCount, rowCount];
        heightBuffer2 = new float[colCount, rowCount];

        float startX = deskBounds.min.x + density / 2;
        float startZ = deskBounds.min.z + density / 2;

        // --- 确保列表被清空 (防止二次运行残留) ---
        robots.Clear();
        originalPositions.Clear();
        targetPositions.Clear(); // 确保从0开始

        for (int x = 0; x < colCount; x++)
        {
            for (int z = 0; z < rowCount; z++)
            {
                Vector3 pos = new Vector3(startX + x * density, deskTopY + robotHeight * 0.5f, startZ + z * density);
                GameObject bot = Instantiate(robotPrefab, pos, Quaternion.identity);
                bot.transform.parent = this.transform;

                robots.Add(bot);
                originalPositions.Add(pos);

                // ✅ 补上了这一行，列表长度就和 robots 一样了，就不会报错了
                targetPositions.Add(pos);
            }
        }

        receiveThread = new Thread(new ThreadStart(ReceiveData));
        receiveThread.IsBackground = true;
        receiveThread.Start();
    }

    void Update()
    {
        // 0. 定期获取障碍物
        if (Time.frameCount % 10 == 0) allBooks = GameObject.FindGameObjectsWithTag("Obstacle");

        FindLargestSafeZone();

        // 1. 计算流体物理 (核心魔法)
        RunFluidSimulation();

        // 2. 根据不同状态，叠加形状
        UpdateFormation();

        // 3. 应用位置和颜色
        ApplyTransformAndColor();
    }

    // --- 🌊 真实流体算法 (Wave Equation) ---
    void RunFluidSimulation()
    {
        float[,] currentBuffer = swapFlag ? heightBuffer2 : heightBuffer1;
        float[,] nextBuffer = swapFlag ? heightBuffer1 : heightBuffer2;

        for (int x = 1; x < colCount - 1; x++)
        {
            for (int z = 1; z < rowCount - 1; z++)
            {
                // 波的传播公式：当前点的新高度受四周邻居高度影响
                // Value = (Neighbors - Current) * Damping
                float val = (currentBuffer[x - 1, z] +
                             currentBuffer[x + 1, z] +
                             currentBuffer[x, z - 1] +
                             currentBuffer[x, z + 1]) / 2.0f;

                val -= nextBuffer[x, z];
                val *= fluidDamping; // 阻尼衰减

                nextBuffer[x, z] = val;
            }
        }
        swapFlag = !swapFlag; // 交换缓冲区，为下一帧做准备
    }

    // --- 在流体上施加力 ---
    void AddRipple(int x, int z, float strength, int radius)
    {
        if (x >= radius && x < colCount - radius && z >= radius && z < rowCount - radius)
        {
            float[,] targetBuffer = swapFlag ? heightBuffer2 : heightBuffer1;
            // 简单的圆形波源
            targetBuffer[x, z] += strength;
            targetBuffer[x + 1, z] += strength * 0.5f;
            targetBuffer[x - 1, z] += strength * 0.5f;
            targetBuffer[x, z + 1] += strength * 0.5f;
            targetBuffer[x, z - 1] += strength * 0.5f;
        }
    }

    void UpdateFormation()
    {
        string activeState = GetActiveState();

        // --- 1. 处理手势交互 (搅动流体) ---
        if (enableHandInteraction)
        {
            if (isLeftHandActive) ApplyHandForce(leftHandPosNorm, 2.0f); // 左手：造波 (正向)
            if (isRightHandActive) ApplyHandForce(rightHandPosNorm, -2.0f); // 右手：吸波 (负向)
        }

        // 鼠标备份
        if (Input.GetMouseButton(0) && !isRightHandActive)
        {
            Vector3 mouseViewport = Camera.main.ScreenToViewportPoint(Input.mousePosition);
            // 这里简单转换一下，鼠标也当做造波
            ApplyHandForce(new Vector2(mouseViewport.x, mouseViewport.y), -2.0f);
        }

        // --- 2. 处理自动状态波纹 ---
        if (activeState == "HAPPY")
        {
            // [Happy 模式]：雨滴效果
            // 每几帧随机落下一滴雨
            if (Random.Range(0, 20) == 0)
            {
                int rx = Random.Range(2, colCount - 2);
                int rz = Random.Range(2, rowCount - 2);
                AddRipple(rx, rz, 1.5f, 1);
            }
        }
        else if (activeState == "NORMAL")
        {
            // [Normal 模式]：什么都不做！
            // 流体算法会自动应用阻尼，波浪会慢慢平息，变成完美的镜面。
        }

        // --- 3. 最终高度计算 ---
        float[,] displayBuffer = swapFlag ? heightBuffer2 : heightBuffer1;

        for (int x = 0; x < colCount; x++)
        {
            for (int z = 0; z < rowCount; z++)
            {
                int index = x * rowCount + z;
                if (index >= robots.Count) continue;

                Vector3 targetPos = originalPositions[index];
                float yOffset = 0;

                // 叠加流体高度 (无论什么学科，流体都是底层的物理层)
                // 限制流体幅度，别飞太高
                float fluidH = Mathf.Clamp(displayBuffer[x, z], -1.5f, 1.5f);
                yOffset += fluidH;

                // 叠加学科形状 (Confused/Physics etc.)
                if (activeState == "CONFUSED")
                {
                    yOffset += CalculateSubjectShape(x, z, activeState);
                }
                else if (activeState == "SLEEPY")
                {
                    // 睡觉时，微微的规律起伏，不走流体，走呼吸
                    yOffset = Mathf.Sin(x * 0.2f + Time.time) * 0.2f;
                }

                targetPos.y += yOffset;
                targetPositions[index] = targetPos;
            }
        }
    }

    // 辅助：把归一化坐标(0~1) 转换为 网格坐标(x, z) 并施加力
    void ApplyHandForce(Vector2 normPos, float strength)
    {
        // 映射 0~1 到 0~colCount
        // 注意：MediaPipe X轴反转问题已经在Python处理还是Unity？
        // 这里的normPos.x: 0是左，1是右。
        // 我们的网格 x: 0是左，colCount是右。
        int gx = Mathf.FloorToInt((1.0f - normPos.x) * colCount); // 镜像X
        int gz = Mathf.FloorToInt(normPos.y * rowCount);

        AddRipple(gx, gz, strength, 2);
    }

    float CalculateSubjectShape(int x, int z, string state)
    {
        int index = x * rowCount + z;
        Vector3 pos = originalPositions[index];
        float yVal = 0;

        // 书本避障检测
        if (IsCloseToAnyBook(pos)) return 0;

        if (currentSubject == Subject.Physics)
        {
            // 物理：引力坑
            float totalGravity = 0;
            if (allBooks != null)
            {
                foreach (var book in allBooks)
                {
                    float dist = Vector2.Distance(new Vector2(pos.x, pos.z), new Vector2(book.transform.position.x, book.transform.position.z));
                    totalGravity += 0.6f / (dist * dist + 0.1f);
                }
            }
            yVal = -Mathf.Clamp(totalGravity, 0, 1.8f);
        }
        else if (currentSubject == Subject.Geography)
        {
            // 地理：基于SafeZone造山
            if (maxSafeRadius > 0.3f)
            {
                float lx = pos.x - bestSpawnCenter.x;
                float lz = pos.z - bestSpawnCenter.z;
                float dist = Mathf.Sqrt(lx * lx + lz * lz);
                if (dist < maxSafeRadius)
                {
                    // 简单的山包
                    yVal = (maxSafeRadius - dist) * 0.5f;
                }
            }
        }
        else if (currentSubject == Subject.Math)
        {
            // 数学：马鞍面
            if (maxSafeRadius > 0.3f)
            {
                float lx = pos.x - bestSpawnCenter.x;
                float lz = pos.z - bestSpawnCenter.z;
                float dist = Mathf.Sqrt(lx * lx + lz * lz);
                if (dist < maxSafeRadius)
                {
                    float nx = lx / maxSafeRadius; float nz = lz / maxSafeRadius;
                    yVal = (nx * nx - nz * nz) * 0.5f + 0.5f;
                }
            }
        }

        return yVal;
    }

    void ApplyTransformAndColor()
    {
        for (int i = 0; i < robots.Count; i++)
        {
            // 移动
            robots[i].transform.position = Vector3.Lerp(robots[i].transform.position, targetPositions[i], Time.deltaTime * 5.0f);

            // 颜色
            Color finalColor = GetBaseColor();
            float heightDiff = robots[i].transform.position.y - originalPositions[i].y;

            if (currentSubject == Subject.Geography && Mathf.Abs(heightDiff) > 0.05f)
            {
                // 分层设色
                float h = Mathf.Clamp01(heightDiff / 1.5f);
                if (h < 0.2f) finalColor = new Color(0.1f, 0.6f, 0.1f); // 绿
                else if (h < 0.5f) finalColor = new Color(0.8f, 0.7f, 0.2f); // 黄
                else if (h < 0.8f) finalColor = new Color(0.5f, 0.3f, 0.1f); // 褐
                else finalColor = Color.white; // 雪
            }

            if (robots[i].GetComponent<Renderer>() != null)
                robots[i].GetComponent<Renderer>().material.color = finalColor;
        }
    }

    // ... (以下辅助函数保持不变：ReceiveData, MapHandToDesk, FindLargestSafeZone, IsCloseToAnyBook, OnGUI 等) ...
    // 为了节省篇幅，请确保保留之前脚本中的 ReceiveData, MapHandToDesk, FindLargestSafeZone, IsCloseToAnyBook
    // 这里我只把变动最大的 MapHandToDesk 和 ReceiveData 再贴一次确保兼容

    // ⚠️ 记得把原来的 ReceiveData 和 OnGUI 复制回来，或者直接用下面的：

    string GetActiveState() { return useManualControl ? manualState : currentState; }

    Vector3 MapHandToDesk(Vector2 normPos)
    {
        // 简单映射，用于 FindLargestSafeZone 等辅助计算
        float xPercent = 1.0f - normPos.x;
        float yPercent = normPos.y;
        float worldX = Mathf.Lerp(deskMinX, deskMaxX, xPercent);
        float worldZ = Mathf.Lerp(deskMaxZ, deskMinZ, yPercent);
        return new Vector3(worldX, 0, worldZ);
    }

    void FindLargestSafeZone()
    {
        // (保持原样，略)
        // 简单起见，这里假设你保留了上面的逻辑。如果丢失，请从上一个代码块复制。
        // 为防万一，我给你个简化的：
        float maxDistFound = 0f;
        Vector3 bestPos = Vector3.zero;
        if (allBooks == null) return;
        int step = 3;
        for (int i = 0; i < robots.Count; i += step)
        {
            Vector3 p = originalPositions[i];
            float distToBook = 100f;
            foreach (var book in allBooks)
            {
                float d = Vector2.Distance(new Vector2(p.x, p.z), new Vector2(book.transform.position.x, book.transform.position.z));
                d -= 0.6f; if (d < distToBook) distToBook = d;
            }
            if (distToBook > maxDistFound) { maxDistFound = distToBook; bestPos = p; }
        }
        bestSpawnCenter = bestPos; maxSafeRadius = maxDistFound;
        if (maxSafeRadius > 3f) maxSafeRadius = 3f; if (maxSafeRadius < 0) maxSafeRadius = 0;
    }

    bool IsCloseToAnyBook(Vector3 pos)
    {
        if (allBooks == null) return false;
        foreach (var book in allBooks)
        {
            if (Vector2.Distance(new Vector2(pos.x, pos.z), new Vector2(book.transform.position.x, book.transform.position.z)) < 0.6f) return true;
        }
        return false;
    }

    Color GetBaseColor()
    {
        string state = GetActiveState();
        if (state == "HAPPY") return Color.green;
        if (state == "CONFUSED") return new Color(0.1f, 0.1f, 0.1f);
        if (state == "SLEEPY") return new Color(1f, 0.3f, 0f);
        return new Color(0, 0.5f, 1f);
    }

    private void ReceiveData()
    {
        try
        {
            client = new UdpClient(port);
            while (true)
            {
                try
                {
                    IPEndPoint anyIP = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = client.Receive(ref anyIP);
                    string message = Encoding.UTF8.GetString(data);

                    if (message.StartsWith("SUB:"))
                    {
                        string subName = message.Substring(4);
                        if (subName == "Math") currentSubject = Subject.Math;
                        else if (subName == "Physics") currentSubject = Subject.Physics;
                        else if (subName == "Geography") currentSubject = Subject.Geography;
                        else if (subName == "History") currentSubject = Subject.History;
                        useManualControl = false;
                    }
                    else if (message.StartsWith("HAND_L:"))
                    {
                        string coordStr = message.Substring(7);
                        if (coordStr == "NONE") isLeftHandActive = false;
                        else
                        {
                            string[] coords = coordStr.Split(',');
                            if (coords.Length == 2)
                            {
                                leftHandPosNorm = new Vector2(float.Parse(coords[0]), float.Parse(coords[1]));
                                isLeftHandActive = true;
                            }
                        }
                    }
                    else if (message.StartsWith("HAND_R:"))
                    {
                        string coordStr = message.Substring(7);
                        if (coordStr == "NONE") isRightHandActive = false;
                        else
                        {
                            string[] coords = coordStr.Split(',');
                            if (coords.Length == 2)
                            {
                                rightHandPosNorm = new Vector2(float.Parse(coords[0]), float.Parse(coords[1]));
                                isRightHandActive = true;
                            }
                        }
                    }
                    else { currentState = message; }
                }
                catch { }
            }
        }
        catch { }
    }

    void OnApplicationQuit() { if (receiveThread != null) receiveThread.Abort(); if (client != null) client.Close(); }

    void OnGUI()
    {
        GUIStyle style = new GUIStyle(GUI.skin.button); style.fontSize = 14;
        GUI.Box(new Rect(10, 10, 260, 500), "未来教学控制台");
        if (useManualControl)
        {
            GUI.backgroundColor = new Color(1, 0.4f, 0.4f);
            if (GUI.Button(new Rect(20, 40, 240, 40), "🛑 模式: 手动演示中", style)) useManualControl = false;
        }
        else
        {
            GUI.backgroundColor = new Color(0.4f, 1, 0.4f);
            if (GUI.Button(new Rect(20, 40, 240, 40), "🤖 模式: AI 情感同步", style)) useManualControl = true;
        }
        GUI.backgroundColor = Color.white;
        enableHandInteraction = GUI.Toggle(new Rect(20, 85, 200, 20), enableHandInteraction, "🖐️ 启用手势控制");
        GUI.Label(new Rect(20, 110, 200, 20), "1. 选择课程主题:");
        GUI.backgroundColor = (currentSubject == Subject.Geography) ? Color.cyan : Color.white;
        if (GUI.Button(new Rect(20, 135, 115, 40), "🌍 地理")) currentSubject = Subject.Geography;
        GUI.backgroundColor = (currentSubject == Subject.Math) ? Color.cyan : Color.white;
        if (GUI.Button(new Rect(145, 135, 115, 40), "📐 数学")) currentSubject = Subject.Math;
        GUI.backgroundColor = (currentSubject == Subject.Physics) ? Color.cyan : Color.white;
        if (GUI.Button(new Rect(20, 185, 115, 40), "⚛️ 物理")) currentSubject = Subject.Physics;
        GUI.backgroundColor = (currentSubject == Subject.History) ? Color.cyan : Color.white;
        if (GUI.Button(new Rect(145, 185, 115, 40), "🏛️ 历史")) currentSubject = Subject.History;
        GUI.backgroundColor = Color.white;
        GUI.Label(new Rect(20, 240, 200, 20), "2. 触发状态:");
        if (useManualControl)
        {
            if (GUI.Button(new Rect(20, 270, 240, 30), "😐 Normal")) manualState = "NORMAL";
            if (GUI.Button(new Rect(20, 310, 240, 30), "😁 Happy")) manualState = "HAPPY";
            GUI.backgroundColor = Color.yellow;
            if (GUI.Button(new Rect(20, 350, 240, 50), "🤔 Confused (自适应)", style)) manualState = "CONFUSED";
            GUI.backgroundColor = Color.red;
            if (GUI.Button(new Rect(20, 410, 240, 30), "😴 Sleepy")) manualState = "SLEEPY";
        }
        else
        {
            string handStatus = (isLeftHandActive ? "L " : "") + (isRightHandActive ? "R" : "");
            GUI.Label(new Rect(20, 270, 240, 100), $"AI 监听中...\n情感: {currentState}\n手势: {handStatus}");
        }
    }
}
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
    [Range(0.9f, 0.999f)] public float fluidDamping = 0.96f;
    public float waveSpeed = 10.0f;

    // 内部数据
    private List<GameObject> robots = new List<GameObject>();
    private List<Vector3> originalPositions = new List<Vector3>();
    private List<Vector3> targetPositions = new List<Vector3>();

    // 流体核心变量
    private float[,] heightBuffer1;
    private float[,] heightBuffer2;
    private bool swapFlag = false;

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

        heightBuffer1 = new float[colCount, rowCount];
        heightBuffer2 = new float[colCount, rowCount];

        float startX = deskBounds.min.x + density / 2;
        float startZ = deskBounds.min.z + density / 2;

        foreach (var bot in robots) { if (bot != null) Destroy(bot); }
        robots.Clear();
        originalPositions.Clear();
        targetPositions.Clear();

        for (int x = 0; x < colCount; x++)
        {
            for (int z = 0; z < rowCount; z++)
            {
                Vector3 pos = new Vector3(startX + x * density, deskTopY + robotHeight * 0.5f, startZ + z * density);
                GameObject bot = Instantiate(robotPrefab, pos, Quaternion.identity);
                bot.transform.parent = this.transform;
                robots.Add(bot);
                originalPositions.Add(pos);
                targetPositions.Add(pos);
            }
        }

        if (receiveThread != null && receiveThread.IsAlive) receiveThread.Abort();
        receiveThread = new Thread(new ThreadStart(ReceiveData));
        receiveThread.IsBackground = true;
        receiveThread.Start();
    }

    void Update()
    {
        if (Time.frameCount % 10 == 0) allBooks = GameObject.FindGameObjectsWithTag("Obstacle");

        FindLargestSafeZone();
        RunFluidSimulation();
        UpdateFormation();
        ApplyTransformAndColor();
    }

    // --- 流体算法 ---
    void RunFluidSimulation()
    {
        float[,] currentBuffer = swapFlag ? heightBuffer2 : heightBuffer1;
        float[,] nextBuffer = swapFlag ? heightBuffer1 : heightBuffer2;

        for (int x = 1; x < colCount - 1; x++)
        {
            for (int z = 1; z < rowCount - 1; z++)
            {
                float val = (currentBuffer[x - 1, z] +
                             currentBuffer[x + 1, z] +
                             currentBuffer[x, z - 1] +
                             currentBuffer[x, z + 1]) / 2.0f;
                val -= nextBuffer[x, z];
                val *= fluidDamping;
                nextBuffer[x, z] = val;
            }
        }

        // 边界归零 (防鬼畜)
        for (int x = 0; x < colCount; x++) { nextBuffer[x, 0] = 0; nextBuffer[x, rowCount - 1] = 0; }
        for (int z = 0; z < rowCount; z++) { nextBuffer[0, z] = 0; nextBuffer[colCount - 1, z] = 0; }

        swapFlag = !swapFlag;
    }

    void AddRipple(int x, int z, float strength, int radius)
    {
        if (x >= radius && x < colCount - radius && z >= radius && z < rowCount - radius)
        {
            float[,] targetBuffer = swapFlag ? heightBuffer2 : heightBuffer1;
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

        // 手势与鼠标
        if (enableHandInteraction)
        {
            if (isLeftHandActive) ApplyHandForce(leftHandPosNorm, 2.0f);
            if (isRightHandActive) ApplyHandForce(rightHandPosNorm, -2.0f);
        }
        if (Input.GetMouseButton(0) && !isRightHandActive)
        {
            Vector3 mouseViewport = Camera.main.ScreenToViewportPoint(Input.mousePosition);
            ApplyHandForce(new Vector2(mouseViewport.x, mouseViewport.y), -2.0f);
        }

        // Happy 雨滴
        if (activeState == "HAPPY")
        {
            if (Random.Range(0, 50) == 0)
            {
                int rx = Random.Range(2, colCount - 2);
                int rz = Random.Range(2, rowCount - 2);
                AddRipple(rx, rz, 1.5f, 1);
            }
        }

        float[,] displayBuffer = swapFlag ? heightBuffer2 : heightBuffer1;

        for (int x = 0; x < colCount; x++)
        {
            for (int z = 0; z < rowCount; z++)
            {
                int index = x * rowCount + z;
                if (index >= robots.Count) continue;

                Vector3 targetPos = originalPositions[index];
                float yOffset = 0;

                float fluidH = Mathf.Clamp(displayBuffer[x, z], -1.5f, 1.5f);
                yOffset += fluidH;

                if (activeState == "CONFUSED")
                {
                    yOffset += CalculateSubjectShape(x, z, activeState);
                }
                else if (activeState == "SLEEPY")
                {
                    yOffset += Mathf.Sin(x * 0.2f + Time.time) * 0.2f;
                }

                targetPos.y += yOffset;
                targetPositions[index] = targetPos;
            }
        }
    }

    void ApplyHandForce(Vector2 normPos, float strength)
    {
        int gx = Mathf.FloorToInt((1.0f - normPos.x) * colCount);
        int gz = Mathf.FloorToInt(normPos.y * rowCount);
        gx = Mathf.Clamp(gx, 2, colCount - 3);
        gz = Mathf.Clamp(gz, 2, rowCount - 3);
        AddRipple(gx, gz, strength, 2);
    }

    // --- 📌 核心修复区：调整了形状参数 ---
    float CalculateSubjectShape(int x, int z, string state)
    {
        int index = x * rowCount + z;
        Vector3 pos = originalPositions[index];
        float yVal = 0;

        if (IsCloseToAnyBook(pos)) return 0;

        // 1. 物理 (Physics): 引力深坑
        if (currentSubject == Subject.Physics)
        {
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
        // 2. 地理 (Geography): 宏伟山脉
        else if (currentSubject == Subject.Geography)
        {
            if (maxSafeRadius > 0.3f)
            {
                float lx = pos.x - bestSpawnCenter.x;
                float lz = pos.z - bestSpawnCenter.z;
                float dist = Mathf.Sqrt(lx * lx + lz * lz);

                // [修复点] 之前 2.0f 频率太高像"一坨"，现在改成 0.5f，山脉更舒展
                if (dist < maxSafeRadius)
                {
                    float noise = Mathf.PerlinNoise(pos.x * 0.5f + Time.time * 0.05f, pos.z * 0.5f);
                    // 高度加倍 (* 1.5f)，看着更壮观
                    yVal = noise * 1.5f * Mathf.SmoothStep(1.0f, 0.0f, dist / maxSafeRadius);
                }
            }
        }
        // 3. 数学 (Math): 悬浮马鞍面
        else if (currentSubject == Subject.Math)
        {
            if (maxSafeRadius > 0.3f)
            {
                float lx = pos.x - bestSpawnCenter.x;
                float lz = pos.z - bestSpawnCenter.z;
                float dist = Mathf.Sqrt(lx * lx + lz * lz);
                if (dist < maxSafeRadius)
                {
                    float nx = lx / maxSafeRadius;
                    float nz = lz / maxSafeRadius;
                    // [修复点] 整体抬升 +0.8f，绝不沉底！
                    yVal = ((nx * nx) - (nz * nz)) * 0.8f + 0.8f;
                }
            }
        }
        // 4. 历史 (History): 金字塔
        else if (currentSubject == Subject.History)
        {
            if (maxSafeRadius > 0.3f)
            {
                float lx = Mathf.Abs(pos.x - bestSpawnCenter.x);
                float lz = Mathf.Abs(pos.z - bestSpawnCenter.z);
                float distSquare = Mathf.Max(lx, lz);
                if (distSquare < maxSafeRadius)
                {
                    yVal = (maxSafeRadius - distSquare) * 1.0f; // 更加挺拔
                }
            }
        }
        return yVal;
    }

    void ApplyTransformAndColor()
    {
        for (int i = 0; i < robots.Count; i++)
        {
            robots[i].transform.position = Vector3.Lerp(robots[i].transform.position, targetPositions[i], Time.deltaTime * 5.0f);

            Color finalColor = GetBaseColor();
            float heightDiff = robots[i].transform.position.y - originalPositions[i].y;

            // 地理：分层设色
            if (currentSubject == Subject.Geography && Mathf.Abs(heightDiff) > 0.05f)
            {
                float h = Mathf.Clamp01(heightDiff / 1.5f); // 适配新的高度
                if (h < 0.2f) finalColor = new Color(0.1f, 0.6f, 0.1f); // 绿
                else if (h < 0.5f) finalColor = new Color(0.8f, 0.7f, 0.2f); // 黄
                else if (h < 0.8f) finalColor = new Color(0.5f, 0.3f, 0.1f); // 褐
                else finalColor = Color.white; // 雪
            }
            // 历史：金字塔 金色
            else if (currentSubject == Subject.History && heightDiff > 0.05f)
            {
                float h = Mathf.Clamp01(heightDiff / 1.5f);
                finalColor = Color.Lerp(new Color(0.6f, 0.4f, 0.2f), new Color(1.0f, 0.8f, 0.0f), h);
            }
            // 数学：马鞍面 霓虹
            else if (currentSubject == Subject.Math && Mathf.Abs(heightDiff) > 0.05f)
            {
                float h = Mathf.Clamp01(Mathf.Abs(heightDiff) / 1.0f);
                finalColor = Color.Lerp(Color.cyan, Color.magenta, h);
            }
            // 物理：红
            else if (currentSubject == Subject.Physics && heightDiff < -0.05f)
            {
                finalColor = Color.Lerp(new Color(0.1f, 0, 0), Color.red, Mathf.Abs(heightDiff));
            }

            if (robots[i].GetComponent<Renderer>() != null)
                robots[i].GetComponent<Renderer>().material.color = finalColor;
        }
    }

    void FindLargestSafeZone()
    {
        float maxDistFound = 0f;
        Vector3 bestPos = Vector3.zero;

        if (allBooks == null || allBooks.Length == 0)
        {
            bestSpawnCenter = new Vector3((deskMinX + deskMaxX) / 2, 0, (deskMinZ + deskMaxZ) / 2);
            maxSafeRadius = Mathf.Min(deskMaxX - deskMinX, deskMaxZ - deskMinZ) / 3.0f;
            return;
        }

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
            float distToEdgeX = Mathf.Min(Mathf.Abs(p.x - deskMinX), Mathf.Abs(p.x - deskMaxX));
            float distToEdgeZ = Mathf.Min(Mathf.Abs(p.z - deskMinZ), Mathf.Abs(p.z - deskMaxZ));
            float distToEdge = Mathf.Min(distToEdgeX, distToEdgeZ);
            float finalSafeRadius = Mathf.Min(distToBook, distToEdge);
            if (finalSafeRadius > maxDistFound) { maxDistFound = finalSafeRadius; bestPos = p; }
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

    string GetActiveState()
    {
        return useManualControl ? manualState : currentState;
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

    Vector3 MapHandToDesk(Vector2 normPos)
    {
        float xPercent = 1.0f - normPos.x;
        float yPercent = normPos.y;
        float worldX = Mathf.Lerp(deskMinX, deskMaxX, xPercent);
        float worldZ = Mathf.Lerp(deskMaxZ, deskMinZ, yPercent);
        return new Vector3(worldX, 0, worldZ);
    }
}
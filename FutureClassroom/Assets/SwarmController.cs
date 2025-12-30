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
    public bool useManualControl = true; // ⚠️ 注意：Python连接时，建议把这个取消勾选，或者点击UI切换模式

    // 内部数据
    private List<GameObject> robots = new List<GameObject>();
    private List<Vector3> originalPositions = new List<Vector3>();
    private List<Vector3> targetPositions = new List<Vector3>();
    private GameObject[] allBooks;

    // 自适应变量
    private Vector3 bestSpawnCenter;
    private float maxSafeRadius;
    private float robotHeight;

    // 网络
    Thread receiveThread;
    UdpClient client;
    public int port = 5005;

    // 状态变量
    private string currentState = "NORMAL";
    private string manualState = "NORMAL";

    private int rowCount;
    private int colCount;
    private float moveSpeed = 5.0f;
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

        float startX = deskBounds.min.x + density / 2;
        float startZ = deskBounds.min.z + density / 2;

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

        receiveThread = new Thread(new ThreadStart(ReceiveData));
        receiveThread.IsBackground = true;
        receiveThread.Start();
    }

    void Update()
    {
        allBooks = GameObject.FindGameObjectsWithTag("Obstacle");
        FindLargestSafeZone();
        UpdateFormation();

        for (int i = 0; i < robots.Count; i++)
        {
            robots[i].transform.position = Vector3.Lerp(robots[i].transform.position, targetPositions[i], Time.deltaTime * moveSpeed);

            // --- 智能色彩 ---
            Color finalColor = GetBaseColor();
            float heightDiff = robots[i].transform.position.y - originalPositions[i].y;

            if (currentSubject == Subject.Physics && GetActiveState() == "CONFUSED")
            {
                float depth = Mathf.Abs(heightDiff);
                if (depth > 0.05f) finalColor = Color.Lerp(new Color(0.1f, 0, 0), Color.red, depth / 1.5f);
            }
            else if (Mathf.Abs(heightDiff) > 0.01f)
            {
                float h = Mathf.Clamp01(Mathf.Abs(heightDiff) / (maxSafeRadius * 0.8f));
                if (currentSubject == Subject.Geography) finalColor = Color.Lerp(Color.green, new Color(0.6f, 0.4f, 0.2f), h);
                else if (currentSubject == Subject.Math) finalColor = Color.Lerp(Color.cyan, Color.magenta, h);
                else if (currentSubject == Subject.History) finalColor = Color.Lerp(new Color(0.6f, 0.4f, 0.2f), Color.yellow, h);
            }

            if (robots[i].GetComponent<Renderer>() != null)
                robots[i].GetComponent<Renderer>().material.color = finalColor;
        }
    }

    string GetActiveState()
    {
        return useManualControl ? manualState : currentState;
    }

    void UpdateFormation()
    {
        string activeState = GetActiveState();

        // 鼠标交互
        Vector3 mouseImpactPos = Vector3.zero;
        bool isMouseDown = Input.GetMouseButton(0);
        if (isMouseDown)
        {
            Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            RaycastHit hit;
            if (Physics.Raycast(ray, out hit)) mouseImpactPos = hit.point;
        }

        for (int i = 0; i < robots.Count; i++)
        {
            Vector3 newPos = originalPositions[i];
            float yOffset = 0;

            if (activeState == "CONFUSED")
            {
                switch (currentSubject)
                {
                    case Subject.Physics:
                        float totalGravity = 0;
                        foreach (var book in allBooks)
                        {
                            float dist = Vector3.Distance(originalPositions[i], book.transform.position);
                            float gravity = 0.6f / (dist * dist + 0.1f);
                            totalGravity += gravity;
                        }
                        yOffset = -Mathf.Clamp(totalGravity, 0, 1.8f);
                        break;

                    case Subject.Geography:
                    case Subject.Math:
                    case Subject.History:
                        if (maxSafeRadius > 0.3f)
                        {
                            float lx = originalPositions[i].x - bestSpawnCenter.x;
                            float lz = originalPositions[i].z - bestSpawnCenter.z;
                            float distCircle = Mathf.Sqrt(lx * lx + lz * lz);
                            float distSquare = Mathf.Max(Mathf.Abs(lx), Mathf.Abs(lz));

                            if (currentSubject == Subject.Geography)
                            {
                                if (distCircle < maxSafeRadius)
                                {
                                    float noise = Mathf.PerlinNoise(originalPositions[i].x * 0.6f + Time.time * 0.05f, originalPositions[i].z * 0.6f);
                                    yOffset = noise * (maxSafeRadius * 0.8f);
                                    yOffset *= Mathf.SmoothStep(1.0f, 0.0f, distCircle / maxSafeRadius);
                                }
                            }
                            else if (currentSubject == Subject.Math)
                            {
                                if (distCircle < maxSafeRadius)
                                {
                                    float nx = lx / maxSafeRadius;
                                    float nz = lz / maxSafeRadius;
                                    float val = (nx * nx) - (nz * nz);
                                    yOffset = val * (maxSafeRadius * 0.8f);
                                    yOffset += maxSafeRadius * 0.5f;
                                }
                            }
                            else if (currentSubject == Subject.History)
                            {
                                if (distSquare < maxSafeRadius)
                                {
                                    float linearHeight = maxSafeRadius - distSquare;
                                    yOffset = Mathf.Floor(linearHeight / robotHeight) * robotHeight;
                                }
                            }
                        }
                        break;
                }
                if (IsCloseToAnyBook(originalPositions[i])) yOffset = 0;
            }
            else if (activeState == "SLEEPY")
            {
                float wave = Mathf.Sin(originalPositions[i].x + Time.time) * 0.2f;
                yOffset = wave;
            }

            if (isMouseDown)
            {
                float distToMouse = Vector3.Distance(originalPositions[i], mouseImpactPos);
                if (distToMouse < 1.0f)
                {
                    float mouseEffect = -0.5f * (1.0f - distToMouse / 1.0f);
                    yOffset += mouseEffect;
                }
            }

            if (activeState == "HAPPY" || activeState == "NORMAL")
            {
                float ripple = Mathf.Sin(Vector3.Distance(Vector3.zero, originalPositions[i]) - Time.time * 2f);
                yOffset += ripple * 0.05f;
            }

            newPos.y += yOffset;
            targetPositions[i] = newPos;
        }
    }

    void FindLargestSafeZone()
    {
        float maxDistFound = 0f;
        Vector3 bestPos = Vector3.zero;

        string activeState = GetActiveState();
        if (activeState != "CONFUSED") return;

        int step = 3;
        for (int i = 0; i < robots.Count; i += step)
        {
            Vector3 p = originalPositions[i];
            float distToBook = 100f;
            foreach (var book in allBooks)
            {
                float d = Vector2.Distance(new Vector2(p.x, p.z), new Vector2(book.transform.position.x, book.transform.position.z));
                d -= 0.6f;
                if (d < distToBook) distToBook = d;
            }
            float distToEdgeX = Mathf.Min(Mathf.Abs(p.x - deskMinX), Mathf.Abs(p.x - deskMaxX));
            float distToEdgeZ = Mathf.Min(Mathf.Abs(p.z - deskMinZ), Mathf.Abs(p.z - deskMaxZ));
            float distToEdge = Mathf.Min(distToEdgeX, distToEdgeZ);
            float finalSafeRadius = Mathf.Min(distToBook, distToEdge);

            if (finalSafeRadius > maxDistFound)
            {
                maxDistFound = finalSafeRadius;
                bestPos = p;
            }
        }
        bestSpawnCenter = bestPos;
        maxSafeRadius = maxDistFound;
        if (maxSafeRadius > 3.0f) maxSafeRadius = 3.0f;
        if (maxSafeRadius < 0f) maxSafeRadius = 0f;
    }

    bool IsCloseToAnyBook(Vector3 pos)
    {
        foreach (var book in allBooks)
        {
            float dist = Vector2.Distance(new Vector2(pos.x, pos.z), new Vector2(book.transform.position.x, book.transform.position.z));
            if (dist < 0.6f) return true;
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

    // --- 核心修改：支持 SUB 指令的接收函数 ---
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

                    // 1. 如果是学科切换指令 (SUB:Math)
                    if (message.StartsWith("SUB:"))
                    {
                        string subName = message.Substring(4); // 截取后面的部分
                        if (subName == "Math") currentSubject = Subject.Math;
                        else if (subName == "Physics") currentSubject = Subject.Physics;
                        else if (subName == "Geography") currentSubject = Subject.Geography;
                        else if (subName == "History") currentSubject = Subject.History;

                        // 收到学科指令后，自动切回 AI 模式，防止卡在手动
                        useManualControl = false;
                    }
                    // 2. 否则是情绪状态 (HAPPY, CONFUSED...)
                    else
                    {
                        currentState = message;
                    }

                }
                catch { }
            }
        }
        catch { }
    }

    void OnApplicationQuit()
    {
        if (receiveThread != null) receiveThread.Abort();
        if (client != null) client.Close();
    }

    void OnGUI()
    {
        GUIStyle style = new GUIStyle(GUI.skin.button);
        style.fontSize = 14;
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
        GUI.Label(new Rect(20, 90, 200, 20), "当前学科 (可手动/AI):");

        // 高亮当前学科
        GUI.backgroundColor = (currentSubject == Subject.Geography) ? Color.cyan : Color.white;
        if (GUI.Button(new Rect(20, 120, 115, 40), "🌍 地理")) currentSubject = Subject.Geography;

        GUI.backgroundColor = (currentSubject == Subject.Math) ? Color.cyan : Color.white;
        if (GUI.Button(new Rect(145, 120, 115, 40), "📐 数学")) currentSubject = Subject.Math;

        GUI.backgroundColor = (currentSubject == Subject.Physics) ? Color.cyan : Color.white;
        if (GUI.Button(new Rect(20, 170, 115, 40), "⚛️ 物理")) currentSubject = Subject.Physics;

        GUI.backgroundColor = (currentSubject == Subject.History) ? Color.cyan : Color.white;
        if (GUI.Button(new Rect(145, 170, 115, 40), "🏛️ 历史")) currentSubject = Subject.History;

        GUI.backgroundColor = Color.white;

        GUI.Label(new Rect(20, 230, 200, 20), "2. 触发状态:");

        if (useManualControl)
        {
            if (GUI.Button(new Rect(20, 260, 240, 30), "😐 Normal")) manualState = "NORMAL";
            if (GUI.Button(new Rect(20, 300, 240, 30), "😁 Happy")) manualState = "HAPPY";
            GUI.backgroundColor = Color.yellow;
            if (GUI.Button(new Rect(20, 340, 240, 50), "🤔 Confused (自适应)", style)) manualState = "CONFUSED";
            GUI.backgroundColor = Color.red;
            if (GUI.Button(new Rect(20, 400, 240, 30), "😴 Sleepy")) manualState = "SLEEPY";
        }
        else
        {
            GUI.Label(new Rect(20, 260, 240, 100), "AI 监听中...\n情感: " + currentState + "\n学科: " + currentSubject);
        }
    }
}
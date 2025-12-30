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

    // 手势交互开关
    public bool enableHandInteraction = true;

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

    // [新增] 双手数据
    private Vector2 leftHandPosNorm = Vector2.zero;
    private bool isLeftHandActive = false;

    private Vector2 rightHandPosNorm = Vector2.zero;
    private bool isRightHandActive = false;

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

            // 物理引力红 / 地形绿 / 其他
            if (currentSubject == Subject.Physics && GetActiveState() == "CONFUSED")
            {
                float depth = Mathf.Abs(heightDiff);
                if (depth > 0.05f) finalColor = Color.Lerp(new Color(0.1f, 0, 0), Color.red, depth / 1.5f);
            }
            else if (Mathf.Abs(heightDiff) > 0.01f)
            {
                float h = Mathf.Clamp01(Mathf.Abs(heightDiff) / (maxSafeRadius * 0.8f));
                // 如果是左手造的山，给它点神圣的金色
                if (heightDiff > 0.2f && isLeftHandActive)
                    finalColor = Color.Lerp(Color.white, Color.yellow, h);
                else if (currentSubject == Subject.Geography) finalColor = Color.Lerp(Color.green, new Color(0.6f, 0.4f, 0.2f), h);
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

        // --- 1. 计算交互点 ---
        Vector3 leftHandWorld = Vector3.zero;
        Vector3 rightHandWorld = Vector3.zero;

        if (enableHandInteraction)
        {
            if (isLeftHandActive) leftHandWorld = MapHandToDesk(leftHandPosNorm);
            if (isRightHandActive) rightHandWorld = MapHandToDesk(rightHandPosNorm);
        }

        // 鼠标备份 (当作右手/引力处理)
        bool isMouseDown = Input.GetMouseButton(0);
        Vector3 mouseWorld = Vector3.zero;
        if (isMouseDown && !isRightHandActive) // 只有右手不在时，鼠标才生效
        {
            Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            RaycastHit hit;
            if (Physics.Raycast(ray, out hit)) mouseWorld = hit.point;
        }

        for (int i = 0; i < robots.Count; i++)
        {
            Vector3 newPos = originalPositions[i];
            float yOffset = 0;

            // --- 基础层：学科形状 ---
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
                                    yOffset = noise * (maxSafeRadius * 0.8f) * Mathf.SmoothStep(1.0f, 0.0f, distCircle / maxSafeRadius);
                                }
                            }
                            else if (currentSubject == Subject.Math)
                            {
                                if (distCircle < maxSafeRadius)
                                {
                                    float nx = lx / maxSafeRadius; float nz = lz / maxSafeRadius;
                                    yOffset = ((nx * nx) - (nz * nz)) * (maxSafeRadius * 0.8f) + maxSafeRadius * 0.5f;
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
                yOffset = Mathf.Sin(originalPositions[i].x + Time.time) * 0.2f;
            }

            // --- 交互层：双极力场 ---
            if (enableHandInteraction)
            {
                // A. 左手 (隆起/山)
                if (isLeftHandActive)
                {
                    float distL = Vector2.Distance(new Vector2(originalPositions[i].x, originalPositions[i].z), new Vector2(leftHandWorld.x, leftHandWorld.z));
                    if (distL < 1.5f)
                    {
                        float lift = 0.8f * Mathf.Cos(distL * 1.5f); // 隆起
                        if (lift > 0) yOffset += lift * (1.0f - distL / 1.5f);
                    }
                }

                // B. 右手 (塌陷/黑洞)
                if (isRightHandActive)
                {
                    float distR = Vector2.Distance(new Vector2(originalPositions[i].x, originalPositions[i].z), new Vector2(rightHandWorld.x, rightHandWorld.z));
                    if (distR < 1.5f)
                    {
                        float sink = -0.8f * Mathf.Cos(distR * 1.5f); // 塌陷
                        if (sink < 0) yOffset += sink * (1.0f - distR / 1.5f);
                    }
                }
            }

            // C. 鼠标备份 (塌陷)
            if (isMouseDown && !isRightHandActive)
            {
                float distM = Vector2.Distance(new Vector2(originalPositions[i].x, originalPositions[i].z), new Vector2(mouseWorld.x, mouseWorld.z));
                if (distM < 1.0f)
                {
                    float mouseEffect = -0.5f * (1.0f - distM / 1.0f);
                    yOffset += mouseEffect;
                }
            }

            if (activeState == "HAPPY" || activeState == "NORMAL")
            {
                yOffset += Mathf.Sin(Vector3.Distance(Vector3.zero, originalPositions[i]) - Time.time * 2f) * 0.05f;
            }

            newPos.y += yOffset;
            targetPositions[i] = newPos;
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
            if (finalSafeRadius > maxDistFound) { maxDistFound = finalSafeRadius; bestPos = p; }
        }
        bestSpawnCenter = bestPos;
        maxSafeRadius = maxDistFound;
        if (maxSafeRadius > 3.0f) maxSafeRadius = 3.0f; if (maxSafeRadius < 0f) maxSafeRadius = 0f;
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

    void OnApplicationQuit()
    {
        if (receiveThread != null) receiveThread.Abort();
        if (client != null) client.Close();
    }

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
        enableHandInteraction = GUI.Toggle(new Rect(20, 85, 200, 20), enableHandInteraction, "🖐️ 启用手势控制 (H键)");
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
            string handStatus = (isLeftHandActive ? "L(山) " : "") + (isRightHandActive ? "R(海)" : "");
            if (handStatus == "") handStatus = "无手势";
            GUI.Label(new Rect(20, 270, 240, 100), $"AI 监听中...\n情感: {currentState}\n手势: {handStatus}");
        }
    }
}
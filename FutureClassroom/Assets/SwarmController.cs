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
    private string currentState = "NORMAL";
    private string manualState = "NORMAL"; 

    private int rowCount;
    private int colCount;
    private float moveSpeed = 5.0f;
    
    private float deskMinX, deskMaxX, deskMinZ, deskMaxZ;

    void Start()
    {
        if (deskSurface == null) { Debug.LogError("❌ 致命错误: 请把 Desk 拖入 Desk Surface 槽位!"); return; }

        // 自动获取你刚才改薄了的高度 (0.08)
        if(robotPrefab != null)
            robotHeight = robotPrefab.transform.localScale.y;
        else
            robotHeight = 0.08f; 

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
                // 确保紧贴桌面生成
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

            // --- 智能色彩 (恢复高科技感) ---
            Color finalColor = GetBaseColor();
            float heightDiff = robots[i].transform.position.y - originalPositions[i].y;
            
            if (currentSubject == Subject.Physics && manualState == "CONFUSED")
            {
                // 物理：深邃黑洞红
                float depth = Mathf.Abs(heightDiff);
                if(depth > 0.05f) finalColor = Color.Lerp(new Color(0.1f, 0, 0), Color.red, depth / 1.5f);
            }
            else if (Mathf.Abs(heightDiff) > 0.01f) 
            {
                float h = Mathf.Clamp01(Mathf.Abs(heightDiff) / (maxSafeRadius * 0.8f));
                
                if(currentSubject == Subject.Geography) 
                    finalColor = Color.Lerp(Color.green, new Color(0.6f, 0.4f, 0.2f), h); 
                else if(currentSubject == Subject.Math) 
                    finalColor = Color.Lerp(Color.cyan, Color.magenta, h); // 赛博数学
                else if(currentSubject == Subject.History) 
                    finalColor = Color.Lerp(new Color(0.6f, 0.4f, 0.2f), Color.yellow, h); 
            }

            if(robots[i].GetComponent<Renderer>() != null)
                robots[i].GetComponent<Renderer>().material.color = finalColor;
        }
    }

    void FindLargestSafeZone()
    {
        float maxDistFound = 0f;
        Vector3 bestPos = Vector3.zero;

        string activeState = useManualControl ? manualState : currentState;
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
        if(maxSafeRadius > 3.0f) maxSafeRadius = 3.0f; 
        if(maxSafeRadius < 0f) maxSafeRadius = 0f;
    }

    Color GetBaseColor()
    {
        string state = useManualControl ? manualState : currentState;
        if (state == "HAPPY") return Color.green;
        if (state == "CONFUSED") return new Color(0.1f, 0.1f, 0.1f); 
        if (state == "SLEEPY") return new Color(1f, 0.3f, 0f); 
        return new Color(0, 0.5f, 1f); 
    }

    void UpdateFormation()
    {
        string activeState = useManualControl ? manualState : currentState;

        for (int i = 0; i < robots.Count; i++)
        {
            if (activeState == "CONFUSED")
            {
                Vector3 newPos = originalPositions[i];
                float yOffset = 0;

                switch (currentSubject)
                {
                    case Subject.Physics:
                        // ⚛️ 物理：引力坑 (恢复完全平滑)
                        float totalGravity = 0;
                        foreach (var book in allBooks)
                        {
                            float dist = Vector3.Distance(originalPositions[i], book.transform.position);
                            // 调整参数，让坑更深、范围更大、更平滑
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
                            float distCircle = Mathf.Sqrt(lx*lx + lz*lz);
                            float distSquare = Mathf.Max(Mathf.Abs(lx), Mathf.Abs(lz));

                            if (currentSubject == Subject.Geography)
                            {
                                // 🌍 地形：Perlin 噪声 (恢复完全平滑)
                                if(distCircle < maxSafeRadius)
                                {
                                    float noise = Mathf.PerlinNoise(originalPositions[i].x * 0.6f + Time.time*0.05f, originalPositions[i].z * 0.6f);
                                    yOffset = noise * (maxSafeRadius * 0.8f); 
                                    // 边缘平滑淡出，不生硬
                                    yOffset *= Mathf.SmoothStep(1.0f, 0.0f, distCircle / maxSafeRadius);
                                }
                            }
                            else if (currentSubject == Subject.Math)
                            {
                                // 📐 数学：马鞍面 (恢复完全平滑)
                                if(distCircle < maxSafeRadius)
                                {
                                    float nx = lx / maxSafeRadius; 
                                    float nz = lz / maxSafeRadius;
                                    // 更夸张的曲面
                                    float val = (nx * nx) - (nz * nz);
                                    yOffset = val * (maxSafeRadius * 0.8f); 
                                    yOffset += maxSafeRadius * 0.5f; 
                                }
                            }
                            else if (currentSubject == Subject.History)
                            {
                                // 🏛️ 历史：金字塔 (依然保持阶梯，但适配薄片)
                                if(distSquare < maxSafeRadius)
                                {
                                    float linearHeight = maxSafeRadius - distSquare;
                                    // 核心修改：使用 0.08 的高度去量化，即使片很薄，也堆得密不透风！
                                    yOffset = Mathf.Floor(linearHeight / robotHeight) * robotHeight;
                                }
                            }
                        }
                        break;
                }
                
                if (IsCloseToAnyBook(originalPositions[i])) yOffset = 0;
                newPos.y += yOffset;
                targetPositions[i] = newPos;
            }
            else if (activeState == "SLEEPY")
            {
                float wave = Mathf.Sin(originalPositions[i].x + Time.time) * 0.2f;
                targetPositions[i] = originalPositions[i] + new Vector3(0, wave, 0);
            }
            else 
            {
                targetPositions[i] = originalPositions[i];
                if (activeState == "HAPPY")
                {
                    float ripple = Mathf.Sin(Vector3.Distance(Vector3.zero, originalPositions[i]) - Time.time * 5f);
                    targetPositions[i] += new Vector3(0, ripple * 0.2f, 0); 
                }
            }
        }
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

    void OnGUI()
    {
        GUIStyle style = new GUIStyle(GUI.skin.button);
        style.fontSize = 14;
        GUI.Box(new Rect(10, 10, 260, 500), "未来教学控制台");

        if (useManualControl) {
            GUI.backgroundColor = new Color(1, 0.4f, 0.4f);
            if (GUI.Button(new Rect(20, 40, 240, 40), "🛑 模式: 手动演示中", style)) useManualControl = false;
        } else {
            GUI.backgroundColor = new Color(0.4f, 1, 0.4f);
            if (GUI.Button(new Rect(20, 40, 240, 40), "🤖 模式: AI 情感同步", style)) useManualControl = true;
        }

        GUI.backgroundColor = Color.white;
        GUI.Label(new Rect(20, 90, 200, 20), "1. 选择课程主题:");

        if (GUI.Button(new Rect(20, 120, 115, 40), "🌍 地理")) currentSubject = Subject.Geography;
        if (GUI.Button(new Rect(145, 120, 115, 40), "📐 数学")) currentSubject = Subject.Math;
        if (GUI.Button(new Rect(20, 170, 115, 40), "⚛️ 物理")) currentSubject = Subject.Physics;
        if (GUI.Button(new Rect(145, 170, 115, 40), "🏛️ 历史")) currentSubject = Subject.History;

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
            GUI.Label(new Rect(20, 260, 240, 100), "Python 监听中...\n状态: " + currentState);
        }
        GUI.Label(new Rect(20, 450, 240, 40), "当前展示: " + currentSubject.ToString());
    }

    private void ReceiveData() {
        try {
            client = new UdpClient(port);
            while (true) {
                try {
                    IPEndPoint anyIP = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = client.Receive(ref anyIP);
                    currentState = Encoding.UTF8.GetString(data);
                } catch { }
            }
        } catch {}
    }

    void OnApplicationQuit() {
        if (receiveThread != null) receiveThread.Abort();
        if (client != null) client.Close();
    }
}
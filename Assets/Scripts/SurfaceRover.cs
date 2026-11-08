using UnityEngine;

/// <summary>
/// 地表巡回模式（步骤 3 配套）：把相机移到**星球表面之上一点、大气球壳之内**，
/// 沿球面漫游，用于查看大气散射在「大气层内」与「大气层外」的不同表现。
///
/// 高度口径（关键）：相机到球心的距离必须 **&gt; 星球表面半径**，否则会跑到星球
/// 网格内部 —— 地表 shader 是 Cull Back，相机在球内时看到的三角形全是背面、
/// 会被整个剔除，结果就是「跳进星球内部、什么都看不到」（实测踩过的坑）。
/// 同时又要 &lt; 大气外壳半径（默认 1.16×R），这样才在大气层内部，
/// raymarch 才会按「相机在球内」的积分起点（tEnter=0）呈现内部散射。
///
/// 默认 roverHeightRatio = 1.03 → 相机在地表上方 3% 半径处，
/// 抬头看天有散射、低头看地有地形，正是「站在地表观察」的构图。
///
/// 与 CameraController 的关系：启用时通过 SetExternalControl(true) 让环绕相机交出
/// Transform 控制权，本类全权写 position/rotation；关闭后环绕逻辑自动恢复。
///
/// 操作：
///   W / S          沿当前朝向前进 / 后退（改球面经纬度）
///   A / D          左右平移（改球面经度）
///   鼠标右键拖拽   转视线（改朝向 yaw / pitch）
///   滚轮           调「离地高度」（相机到球心的距离 / 星球半径）
///   Shift          加速
/// </summary>
[DisallowMultipleComponent]
public class SurfaceRover : MonoBehaviour
{
    [Header("引用")]
    [SerializeField] private CameraController orbitCamera;
    [SerializeField] private EarthManager earthManager;

    [Header("巡回")]
    [SerializeField, Range(1.005f, 1.15f)]
    [Tooltip("相机到球心距离 / 星球半径：必须 >1（在地表之外，否则会被 Cull Back 剔除）、" +
             "<1.16（在大气外壳之内）。默认 1.03 = 地表上方 3% 半径")]
    private float roverHeightRatio = 1.03f;

    /// <summary>滚轮可调的最低/最高高度（相对星球半径）。</summary>
    private const float MinHeightRatio = 1.005f;
    private const float MaxHeightRatio = 1.15f;   // 略低于大气外壳 1.16

    [SerializeField, Range(0.02f, 2f)] private float moveSpeed = 0.35f;   // 弧度/秒
    [SerializeField, Range(60f, 600f)] private float lookSpeed = 200f;     // 度/秒
    [SerializeField, Range(2f, 8f)] private float boostMultiplier = 4f;
    [SerializeField, Range(0.001f, 0.05f)] private float zoomStep = 0.005f;

    private bool _active;
    private float _lat = 20f;     // 纬度（度）
    private float _lon = 0f;      // 经度（度）
    private float _lookYaw = 0f;  // 视线水平角（度）
    private float _lookPitch = 2f;// 视线仰角（度，正值抬头；略抬头让地平线与天空都入画）
    private float _heightRatio;

    private Camera _camera;
    private float _savedNearClip;
    private bool _nearClipAdjusted;

    public bool IsActive => _active;

    #region Unity

    private void Awake()
    {
        if (orbitCamera == null)
            orbitCamera = GetComponent<CameraController>();
        if (earthManager == null)
            earthManager = EarthManager.Instance;
        _camera = GetComponent<Camera>();
        _heightRatio = roverHeightRatio;
    }

    private void Update()
    {
        if (!_active)
            return;

        HandleMove();
        HandleLook();
        HandleZoom();
        ApplyTransform();
    }

    #endregion

    #region 公开接口

    /// <summary>进入 / 退出地表巡回。返回当前是否处于巡回模式。</summary>
    public void SetActive(bool active)
    {
        if (_active == active)
            return;

        _active = active;
        if (orbitCamera != null)
            orbitCamera.SetExternalControl(active);

        if (active)
        {
            if (earthManager == null) earthManager = EarthManager.Instance;
            _heightRatio = Mathf.Clamp(roverHeightRatio, MinHeightRatio, MaxHeightRatio);

            // 贴地表时相机离地表只有百分之几半径，原 near clip（0.3）会把脚下地表
            // 整个裁掉。临时调小，退出时恢复。
            AdjustNearClip(true);

            // 从环绕相机的当前位置反解球面经纬度，避免「一开启就瞬移到别处」
            InitializeFromCurrentPosition();
            ApplyTransform();
            Debug.Log("[rover] 进入地表巡回：相机位于地表上方 " +
                      $"{((_heightRatio - 1f) * 100f):F1}% 半径处（大气层内）；" +
                      $"nearClip 临时设为 {(_camera != null ? _camera.nearClipPlane : -1f):F3}；" +
                      "WASD 移动，右键转视线，滚轮调高度");
        }
        else
        {
            AdjustNearClip(false);
            Debug.Log("[rover] 退出地表巡回，恢复环绕视角与 near clip");
        }
    }

    public void Toggle() => SetActive(!_active);

    private void OnDestroy()
    {
        if (_nearClipAdjusted && _camera != null)
            _camera.nearClipPlane = _savedNearClip;
    }

    #endregion

    #region 内部

    /// <summary>进入巡回时临时缩小 near clip，退出时恢复原值。</summary>
    private void AdjustNearClip(bool entering)
    {
        if (_camera == null)
            _camera = GetComponent<Camera>();
        if (_camera == null)
            return;

        if (entering)
        {
            if (!_nearClipAdjusted)
            {
                _savedNearClip = _camera.nearClipPlane;
                _nearClipAdjusted = true;
            }
            // 星球半径量级 ~2，相机在 1.03R 时离地表约 0.06，0.02 足够看到脚下
            _camera.nearClipPlane = Mathf.Min(_savedNearClip, 0.02f);
        }
        else if (_nearClipAdjusted)
        {
            _camera.nearClipPlane = _savedNearClip;
            _nearClipAdjusted = false;
        }
    }

    private void InitializeFromCurrentPosition()
    {
        Vector3 center = GetPlanetCenter();
        Vector3 dir = (transform.position - center).normalized;
        if (dir.sqrMagnitude < 1e-8f)
            dir = Vector3.forward;

        _lat = Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;
        _lon = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        // 视线：默认朝「北 + 略抬头」，让地平线附近的天空与地表都入画
        _lookYaw = 0f;
        _lookPitch = 2f;
    }

    private Vector3 GetPlanetCenter()
    {
        if (earthManager != null)
            return earthManager.PlanetCenter;
        return Vector3.zero;
    }

    private float GetPlanetRadius()
    {
        // 用「自身网格」的世界半径，避免 Renderer.bounds 被子物体影响而偏大
        if (earthManager != null)
            return earthManager.PlanetSurfaceRadius;
        return 5f;   // 兜底
    }

    private void HandleMove()
    {
        float boost = (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
            ? boostMultiplier : 1f;
        float dt = Time.deltaTime * moveSpeed * boost;

        float forward = 0f, strafe = 0f;
        if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) forward += 1f;
        if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) forward -= 1f;
        if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) strafe += 1f;
        if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) strafe -= 1f;

        if (forward == 0f && strafe == 0f)
            return;

        // 前进沿视线水平方向：纬度变化 = forward*cos(yaw)，经度变化 = forward*sin(yaw)+strafe
        float yawRad = _lookYaw * Mathf.Deg2Rad;
        _lat += forward * Mathf.Cos(yawRad) * dt * Mathf.Rad2Deg;
        _lon += (forward * Mathf.Sin(yawRad) + strafe) * dt * Mathf.Rad2Deg /
                Mathf.Max(0.15f, Mathf.Cos(_lat * Mathf.Deg2Rad));
        _lat = Mathf.Clamp(_lat, -89f, 89f);   // 极点附近经度退化，夹住
        _lon = Mathf.Repeat(_lon + 180f, 360f) - 180f;
    }

    private void HandleLook()
    {
        if (!Input.GetMouseButton(1))
            return;
        _lookYaw += Input.GetAxis("Mouse X") * lookSpeed * Time.deltaTime;
        _lookPitch -= Input.GetAxis("Mouse Y") * lookSpeed * Time.deltaTime;
        _lookPitch = Mathf.Clamp(_lookPitch, -80f, 80f);
        _lookYaw = Mathf.Repeat(_lookYaw + 180f, 360f) - 180f;
    }

    private void HandleZoom()
    {
        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (scroll == 0f)
            return;
        _heightRatio = Mathf.Clamp(_heightRatio - scroll * zoomStep,
                                   MinHeightRatio, MaxHeightRatio);
    }

    private void ApplyTransform()
    {
        Vector3 center = GetPlanetCenter();
        float radius = GetPlanetRadius() * _heightRatio;
        float latR = _lat * Mathf.Deg2Rad, lonR = _lon * Mathf.Deg2Rad;
        float cosLat = Mathf.Cos(latR);

        // 球面法线（从球心指向相机位置）
        Vector3 n = new Vector3(cosLat * Mathf.Sin(lonR), Mathf.Sin(latR), cosLat * Mathf.Cos(lonR));
        transform.position = center + n * radius;

        // 局部基：东(E)、北(N)、上(U=n)
        Vector3 up = n;
        Vector3 east = Vector3.Cross(Vector3.up, up);
        if (east.sqrMagnitude < 1e-6f) east = Vector3.right;   // 极点
        east.Normalize();
        Vector3 north = Vector3.Cross(up, east).normalized;

        float yawR = _lookYaw * Mathf.Deg2Rad, pitchR = _lookPitch * Mathf.Deg2Rad;
        Vector3 lookDir = (north * Mathf.Cos(yawR) + east * Mathf.Sin(yawR)) * Mathf.Cos(pitchR)
                        + up * Mathf.Sin(pitchR);
        transform.rotation = Quaternion.LookRotation(lookDir.normalized, up);
    }

    #endregion
}

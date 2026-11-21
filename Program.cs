using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GameTuner;

static class Program
{
    [STAThread]
    static void Main()
    {
        Updater.CleanupOld();
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

// ───────────────────────── 데이터 ─────────────────────────

record RegVal(RegistryHive Hive, string Path, string Name, object Value, RegistryValueKind Kind)
{
    public string Key => $"{Hive}\\{Path}\\{Name}";
}

class BackupEntry
{
    public bool Existed { get; set; }
    public string Kind { get; set; } = "";
    public string Data { get; set; } = "";
}

class Tweak
{
    public string Title = "";
    public string Desc = "";
    public bool NeedsReboot;
    public bool Aggressive;
    public string Risk = "";
    public Func<SysEnv, string?> AutoSkip = _ => null;   // null = 자동 최적화 시 추천, 문자열 = 제외 사유
    public Func<bool> IsApplied = () => false;
    public Action Apply = () => { };
    public Action Revert = () => { };
}

// ───────────────────────── 업데이트 ─────────────────────────

static class Updater
{
    // TODO: GitHub 계정/저장소 이름으로 변경 (공개 저장소여야 로그인 없이 Release 조회 가능)
    public const string Repo = "RimGa9703/project_GameTuner";
    const string AssetName = "GameTuner.exe";

    public static Version Current
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (v == null)
            {
                return new Version(0, 0, 0);
            }
            return Normalize(v);
        }
    }

    // 1.0.5 와 1.0.5.0 을 같은 버전으로 비교하기 위해 3자리로 맞춤
    static Version Normalize(Version v)
    {
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GameTuner-Updater");
        return http;
    }

    // 새 버전이 있으면 (버전, 다운로드 URL), 없으면 null
    public static async Task<(Version Latest, string Url)?> CheckAsync()
    {
        using var http = CreateClient();
        string json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        using var doc = JsonDocument.Parse(json);

        string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (Version.TryParse(tag.TrimStart('v', 'V'), out var parsed) == false)
        {
            return null;
        }
        var latest = Normalize(parsed);
        if (latest <= Current)
        {
            return null;
        }

        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() == AssetName)
            {
                string? url = asset.GetProperty("browser_download_url").GetString();
                if (url != null)
                {
                    return (latest, url);
                }
            }
        }
        return null;
    }

    // 실행 중인 exe는 덮어쓸 수 없지만 이름 변경은 가능 → .old로 밀어내고 새 파일을 원래 이름으로 둔 뒤 재시작
    public static async Task InstallAsync(string url)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("실행 파일 경로를 알 수 없습니다.");
        string downloaded = exe + ".new";
        string old = exe + ".old";

        using (var http = CreateClient())
        {
            byte[] bytes = await http.GetByteArrayAsync(url);
            // ponytail: 크기만 확인. 서명/해시 검증이 필요해지면 Release에 sha256 파일을 같이 올려 비교
            if (bytes.Length < 1024 * 1024)
            {
                throw new InvalidOperationException("다운로드한 파일이 비정상적으로 작습니다.");
            }
            await File.WriteAllBytesAsync(downloaded, bytes);
        }

        if (File.Exists(old) == true)
        {
            File.Delete(old);
        }
        File.Move(exe, old);
        try
        {
            File.Move(downloaded, exe);
        }
        catch
        {
            File.Move(old, exe);   // 실패 시 원래 파일 복구
            throw;
        }

        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        Application.Exit();
    }

    // 업데이트 직후 실행 시 이전 버전 파일 정리 (이전 프로세스가 아직 종료 중이면 다음 실행 때 지움)
    public static void CleanupOld()
    {
        string? exe = Environment.ProcessPath;
        if (exe == null)
        {
            return;
        }
        try
        {
            if (File.Exists(exe + ".old") == true)
            {
                File.Delete(exe + ".old");
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

// ───────────────────────── PC 환경 감지 ─────────────────────────

class SysEnv
{
    public bool IsLaptop;
    public bool OnAC = true;
    public int Build;
    public bool IsWin11;
    public string SystemDisk = "Unknown";   // SSD / HDD / Unspecified / Unknown
    public bool BitLocker;
    public bool HasStrictAntiCheat;

    public static SysEnv Detect()
    {
        var env = new SysEnv();

        var power = SystemInformation.PowerStatus;
        env.IsLaptop = power.BatteryChargeStatus.HasFlag(BatteryChargeStatus.NoSystemBattery) == false
            && power.BatteryChargeStatus != BatteryChargeStatus.Unknown;
        env.OnAC = power.PowerLineStatus != PowerLineStatus.Offline;

        env.Build = Environment.OSVersion.Version.Build;
        env.IsWin11 = env.Build >= 22000;

        // 디스크 종류·BitLocker는 PowerShell로 한 번에 조회 (출력값은 영문 enum이라 언어 무관)
        const string ps =
            "$n=(Get-Partition -DriveLetter $env:SystemDrive[0]).DiskNumber; " +
            "'disk=' + (Get-PhysicalDisk | Where-Object DeviceId -eq $n).MediaType; " +
            "'bitlocker=' + (Get-BitLockerVolume -MountPoint $env:SystemDrive -ErrorAction SilentlyContinue).ProtectionStatus";
        string output = Native.Run("powershell", $"-NoProfile -ExecutionPolicy Bypass -Command \"{ps}\"").Output;
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("disk=") == true && line.Length > 5)
            {
                env.SystemDisk = line.Substring(5);
            }
            else if (line.StartsWith("bitlocker=") == true)
            {
                env.BitLocker = line.Substring(10) == "On";
            }
        }

        foreach (string svc in new[] { "vgc", "vgk", "FACEIT" })
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{svc}");
            if (key != null)
            {
                env.HasStrictAntiCheat = true;
            }
        }
        return env;
    }

    public override string ToString()
    {
        string device;
        if (IsLaptop == true)
        {
            if (OnAC == true)
            {
                device = "노트북(충전 중)";
            }
            else
            {
                device = "노트북(배터리)";
            }
        }
        else
        {
            device = "데스크톱";
        }

        string os;
        if (IsWin11 == true)
        {
            os = "Windows 11";
        }
        else
        {
            os = "Windows 10";
        }

        string bitlocker;
        if (BitLocker == true)
        {
            bitlocker = "켜짐";
        }
        else
        {
            bitlocker = "꺼짐";
        }

        string antiCheat;
        if (HasStrictAntiCheat == true)
        {
            antiCheat = "감지됨";
        }
        else
        {
            antiCheat = "없음";
        }

        return $"{device} / {os} (빌드 {Build}) / 시스템 드라이브 {SystemDisk} / BitLocker {bitlocker} / 커널 안티치트 {antiCheat}";
    }
}

// ───────────────────────── PC 사양 ─────────────────────────

static class Specs
{
    // CIM으로 한 번에 조회. 값에 한글이 섞일 수 있어 UTF-8로 받음
    // ponytail: GPU 메모리는 Win32_VideoController.AdapterRAM이 4GB에서 잘려서 표시 안 함. 필요하면 레지스트리 HardwareInformation.qwMemorySize로 확장
    const string ps =
        "[Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
        "$c=Get-CimInstance Win32_Processor | Select-Object -First 1; " +
        "'cpu=' + $c.Name.Trim() + '|' + $c.NumberOfCores + '|' + $c.NumberOfLogicalProcessors; " +
        "foreach($g in Get-CimInstance Win32_VideoController){ 'gpu=' + $g.Name + '|' + $g.DriverVersion + '|' + $g.CurrentHorizontalResolution + '|' + $g.CurrentVerticalResolution + '|' + $g.CurrentRefreshRate }; " +
        "$m=@(Get-CimInstance Win32_PhysicalMemory); 'ram=' + ($m | Measure-Object Capacity -Sum).Sum + '|' + $m[0].ConfiguredClockSpeed + '|' + $m.Count; " +
        "$b=Get-CimInstance Win32_BaseBoard; 'board=' + $b.Manufacturer + ' ' + $b.Product; " +
        "$o=Get-CimInstance Win32_OperatingSystem; $v=(Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion').DisplayVersion; 'os=' + $o.Caption + '|' + $v + '|' + $o.BuildNumber; " +
        "foreach($d in Get-PhysicalDisk){ 'disk=' + $d.FriendlyName + '|' + $d.MediaType + '|' + $d.Size }";

    public static string Query()
    {
        string output = Native.Run("powershell", $"-NoProfile -ExecutionPolicy Bypass -Command \"{ps}\"", System.Text.Encoding.UTF8).Output;
        var sb = new System.Text.StringBuilder();
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            string key = line.Substring(0, eq);
            string[] p = line.Substring(eq + 1).Split('|');
            switch (key)
            {
                case "cpu":
                    sb.AppendLine($"CPU        : {p[0]} ({At(p, 1)}코어 / {At(p, 2)}스레드)");
                    break;
                case "gpu":
                    string screen = "";
                    if (At(p, 2) != "" && At(p, 4) != "")
                    {
                        screen = $" — {At(p, 2)}x{At(p, 3)} @ {At(p, 4)}Hz";
                    }
                    sb.AppendLine($"GPU        : {p[0]} (드라이버 {At(p, 1)}){screen}");
                    break;
                case "ram":
                    sb.AppendLine($"RAM        : {Gib(At(p, 0))} ({At(p, 1)} MHz, {At(p, 2)}개)");
                    break;
                case "board":
                    sb.AppendLine($"메인보드   : {p[0]}");
                    break;
                case "os":
                    sb.AppendLine($"Windows    : {p[0]} {At(p, 1)} (빌드 {At(p, 2)})");
                    break;
                case "disk":
                    sb.AppendLine($"디스크     : {p[0]} — {At(p, 1)}, {Gb(At(p, 2))}");
                    break;
            }
        }
        if (sb.Length == 0)
        {
            return "사양을 조회하지 못했습니다.\n" + output.Trim();
        }
        return sb.ToString().TrimEnd();
    }

    static string At(string[] parts, int index)
    {
        if (index < parts.Length)
        {
            return parts[index].Trim();
        }
        return "";
    }

    // RAM은 1024 단위
    static string Gib(string bytes)
    {
        if (long.TryParse(bytes, out long n) == false)
        {
            return "알 수 없음";
        }
        return $"{n / (1024L * 1024 * 1024)} GB";
    }

    // 디스크는 제조사 표기와 같은 1000 단위
    static string Gb(string bytes)
    {
        if (long.TryParse(bytes, out long n) == false)
        {
            return "알 수 없음";
        }
        return $"{n / 1000_000_000L} GB";
    }
}

// ───────────────────────── 백업 ─────────────────────────

static class Backup
{
    public static readonly string FilePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GameTuner", "backup.json");

    static readonly Dictionary<string, BackupEntry> data = Load();

    static Dictionary<string, BackupEntry> Load()
    {
        if (File.Exists(FilePath) == false)
        {
            return new Dictionary<string, BackupEntry>();
        }
        var loaded = JsonSerializer.Deserialize<Dictionary<string, BackupEntry>>(File.ReadAllText(FilePath));
        if (loaded == null)
        {
            return new Dictionary<string, BackupEntry>();
        }
        return loaded;
    }

    public static bool Has(string key) => data.ContainsKey(key);

    public static BackupEntry? Get(string key)
    {
        if (data.TryGetValue(key, out var entry) == true)
        {
            return entry;
        }
        return null;
    }

    public static void Set(string key, BackupEntry entry)
    {
        data[key] = entry;
        Save();
    }

    public static void Remove(string key)
    {
        if (data.Remove(key) == true)
        {
            Save();
        }
    }

    static void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }
}

// ───────────────────────── 레지스트리 ─────────────────────────

static class Reg
{
    static RegistryKey OpenWritable(RegVal v)
    {
        return RegistryKey.OpenBaseKey(v.Hive, RegistryView.Registry64).CreateSubKey(v.Path, true);
    }

    public static bool Matches(RegVal v)
    {
        using var key = RegistryKey.OpenBaseKey(v.Hive, RegistryView.Registry64).OpenSubKey(v.Path);
        if (key == null)
        {
            return false;
        }
        object? current = key.GetValue(v.Name);
        if (current == null)
        {
            return false;
        }
        return Convert.ToString(current) == Convert.ToString(v.Value);
    }

    // 최초 1회만 원래 값을 백업하고 새 값을 쓴다
    public static void Write(RegVal v)
    {
        using var key = OpenWritable(v);
        if (Backup.Has(v.Key) == false)
        {
            object? old = key.GetValue(v.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var entry = new BackupEntry { Existed = old != null };
            if (old != null)
            {
                // ponytail: DWord/String 계열만 직렬화. 대상 값이 전부 이 두 종류라 충분, Binary 대상 추가 시 확장
                entry.Kind = key.GetValueKind(v.Name).ToString();
                entry.Data = Convert.ToString(old) ?? "";
            }
            Backup.Set(v.Key, entry);
        }
        key.SetValue(v.Name, v.Value, v.Kind);
    }

    public static void Restore(RegVal v)
    {
        var entry = Backup.Get(v.Key);
        if (entry == null)
        {
            return;
        }
        using var key = OpenWritable(v);
        if (entry.Existed == true)
        {
            var kind = Enum.Parse<RegistryValueKind>(entry.Kind);
            object value;
            if (kind == RegistryValueKind.DWord)
            {
                value = int.Parse(entry.Data);
            }
            else if (kind == RegistryValueKind.QWord)
            {
                value = long.Parse(entry.Data);
            }
            else
            {
                value = entry.Data;
            }
            key.SetValue(v.Name, value, kind);
        }
        else
        {
            key.DeleteValue(v.Name, false);
        }
        Backup.Remove(v.Key);
    }

    public static int ReadInt(RegistryHive hive, string path, string name, int fallback)
    {
        using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).OpenSubKey(path);
        if (key == null)
        {
            return fallback;
        }
        if (int.TryParse(Convert.ToString(key.GetValue(name)), out int result) == true)
        {
            return result;
        }
        return fallback;
    }
}

// ───────────────────────── 시스템 호출 ─────────────────────────

static class Native
{
    const uint SPI_SETMOUSE = 0x0004;
    const uint SPIF_UPDATEINIFILE = 0x01;
    const uint SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SystemParametersInfo(uint action, uint param, int[] vparam, uint winIni);

    // 레지스트리에 있는 마우스 값을 재로그인 없이 즉시 반영
    public static void ApplyMouseFromRegistry()
    {
        const string path = @"Control Panel\Mouse";
        int[] mouse =
        {
            Reg.ReadInt(RegistryHive.CurrentUser, path, "MouseThreshold1", 6),
            Reg.ReadInt(RegistryHive.CurrentUser, path, "MouseThreshold2", 10),
            Reg.ReadInt(RegistryHive.CurrentUser, path, "MouseSpeed", 1),
        };
        SystemParametersInfo(SPI_SETMOUSE, 0, mouse, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
    }

    // encoding = null이면 시스템 기본 코드페이지 (bcdedit/powercfg 한글 출력은 이걸로 읽어야 함)
    public static (int Code, string Output) Run(string file, string args, System.Text.Encoding? encoding = null)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (encoding != null)
        {
            psi.StandardOutputEncoding = encoding;
            psi.StandardErrorEncoding = encoding;
        }
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }
}

// ───────────────────────── 전원 관리 ─────────────────────────

static class Power
{
    const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    // ponytail: 복제된 '최고의 성능' 플랜은 GUID가 랜덤이라 원본 GUID만 인식. 필요하면 powercfg /list 이름 파싱으로 확장
    const string UltimatePerformance = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    const string GuidPattern = @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";
    const string PrevKey = "power:prev";
    const string AppliedKey = "power:applied";

    public static string? ActiveScheme()
    {
        var m = Regex.Match(Native.Run("powercfg", "/getactivescheme").Output, GuidPattern);
        if (m.Success == true)
        {
            return m.Value.ToLowerInvariant();
        }
        return null;
    }

    public static bool IsApplied()
    {
        string? active = ActiveScheme();
        if (active == null)
        {
            return false;
        }
        // 직접 고성능/최고의 성능 플랜을 켜 둔 경우도 적용됨으로 판단
        if (active == HighPerformance || active == UltimatePerformance)
        {
            return true;
        }
        var applied = Backup.Get(AppliedKey);
        if (applied == null)
        {
            return false;
        }
        return active == applied.Data;
    }

    public static void Apply()
    {
        string? current = ActiveScheme();
        if (Backup.Has(PrevKey) == false && current != null)
        {
            Backup.Set(PrevKey, new BackupEntry { Existed = true, Data = current });
        }

        string target = HighPerformance;
        if (Native.Run("powercfg", $"/setactive {HighPerformance}").Code != 0)
        {
            // 모던 스탠바이 노트북 등 고성능 플랜이 숨겨진 경우 복제해서 사용
            var m = Regex.Match(Native.Run("powercfg", $"/duplicatescheme {HighPerformance}").Output, GuidPattern);
            if (m.Success == false)
            {
                throw new InvalidOperationException("고성능 전원 관리 옵션을 만들 수 없습니다.");
            }
            target = m.Value.ToLowerInvariant();
            if (Native.Run("powercfg", $"/setactive {target}").Code != 0)
            {
                throw new InvalidOperationException("고성능 전원 관리 옵션을 활성화할 수 없습니다.");
            }
        }
        Backup.Set(AppliedKey, new BackupEntry { Existed = true, Data = target });
    }

    public static void Revert()
    {
        var prev = Backup.Get(PrevKey);
        if (prev != null)
        {
            Native.Run("powercfg", $"/setactive {prev.Data}");
        }
        Backup.Remove(PrevKey);
        Backup.Remove(AppliedKey);
    }
}

// 현재 전원 플랜의 개별 설정값 (AC/DC) 변경
static class PowerSetting
{
    static (int Ac, int Dc)? Query(string scheme, string sub, string setting)
    {
        // /qh = 숨김 설정 포함. 출력 마지막 두 16진수가 현재 AC/DC 값 (언어 무관)
        var matches = Regex.Matches(Native.Run("powercfg", $"/qh {scheme} {sub} {setting}").Output, @"0x[0-9a-fA-F]{8}");
        if (matches.Count < 2)
        {
            return null;
        }
        int ac = Convert.ToInt32(matches[matches.Count - 2].Value, 16);
        int dc = Convert.ToInt32(matches[matches.Count - 1].Value, 16);
        return (ac, dc);
    }

    static void Set(string scheme, string sub, string setting, int ac, int dc)
    {
        Native.Run("powercfg", $"/setacvalueindex {scheme} {sub} {setting} {ac}");
        Native.Run("powercfg", $"/setdcvalueindex {scheme} {sub} {setting} {dc}");
        Native.Run("powercfg", $"/setactive {scheme}");
    }

    public static Tweak Make(string title, string desc, string risk, string sub, string setting, int value)
    {
        string key = $"powersetting:{setting}";
        return new Tweak
        {
            Title = title,
            Desc = desc,
            Risk = risk,
            Aggressive = true,
            IsApplied = () =>
            {
                string? scheme = Power.ActiveScheme();
                if (scheme == null)
                {
                    return false;
                }
                var cur = Query(scheme, sub, setting);
                return cur != null && cur.Value.Ac == value && cur.Value.Dc == value;
            },
            Apply = () =>
            {
                string? scheme = Power.ActiveScheme();
                if (scheme == null)
                {
                    throw new InvalidOperationException("현재 전원 플랜을 확인할 수 없습니다.");
                }
                if (Backup.Has(key) == false)
                {
                    var cur = Query(scheme, sub, setting);
                    if (cur == null)
                    {
                        throw new InvalidOperationException("현재 설정값을 읽을 수 없습니다.");
                    }
                    // Kind 필드에 플랜 GUID 저장 → 되돌릴 때 같은 플랜에 복원
                    Backup.Set(key, new BackupEntry { Existed = true, Kind = scheme, Data = $"{cur.Value.Ac},{cur.Value.Dc}" });
                }
                Set(scheme, sub, setting, value, value);
            },
            Revert = () =>
            {
                var entry = Backup.Get(key);
                if (entry == null)
                {
                    return;
                }
                string[] parts = entry.Data.Split(',');
                Set(entry.Kind, sub, setting, int.Parse(parts[0]), int.Parse(parts[1]));
                string? active = Power.ActiveScheme();
                if (active != null)
                {
                    Native.Run("powercfg", $"/setactive {active}");
                }
                Backup.Remove(key);
            },
        };
    }
}

// bcdedit 부팅 옵션 (yes/no 값)
static class Boot
{
    static string? Current(string name)
    {
        var m = Regex.Match(Native.Run("bcdedit", "/enum {current}").Output, $@"^{name}\s+(\S+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (m.Success == false)
        {
            return null;
        }
        string v = m.Groups[1].Value.ToLowerInvariant();
        if (v == "yes" || v == "예")
        {
            return "yes";
        }
        return "no";
    }

    public static Tweak Make(string title, string desc, string risk, string name)
    {
        string key = $"bcd:{name}";
        return new Tweak
        {
            Title = title,
            Desc = desc,
            Risk = risk,
            Aggressive = true,
            NeedsReboot = true,
            IsApplied = () => Current(name) == "yes",
            Apply = () =>
            {
                if (Backup.Has(key) == false)
                {
                    string? cur = Current(name);
                    Backup.Set(key, new BackupEntry { Existed = cur != null, Data = cur ?? "" });
                }
                var r = Native.Run("bcdedit", $"/set {{current}} {name} yes");
                if (r.Code != 0)
                {
                    throw new InvalidOperationException(r.Output.Trim());
                }
            },
            Revert = () =>
            {
                var entry = Backup.Get(key);
                if (entry == null)
                {
                    return;
                }
                if (entry.Existed == true)
                {
                    Native.Run("bcdedit", $"/set {{current}} {name} {entry.Data}");
                }
                else
                {
                    Native.Run("bcdedit", $"/deletevalue {{current}} {name}");
                }
                Backup.Remove(key);
            },
        };
    }
}

// ───────────────────────── 최적화 항목 ─────────────────────────

static class Tweaks
{
    const RegistryHive HKCU = RegistryHive.CurrentUser;
    const RegistryHive HKLM = RegistryHive.LocalMachine;
    const RegistryValueKind DW = RegistryValueKind.DWord;
    const RegistryValueKind SZ = RegistryValueKind.String;

    static Tweak RegTweak(string title, string desc, bool reboot, Func<List<RegVal>> values, Action? after = null, Action? afterRevert = null)
    {
        if (afterRevert == null)
        {
            afterRevert = after;
        }
        return new Tweak
        {
            Title = title,
            Desc = desc,
            NeedsReboot = reboot,
            IsApplied = () =>
            {
                var list = values();
                return list.Count > 0 && list.All(Reg.Matches);
            },
            Apply = () =>
            {
                var list = values();
                if (list.Count == 0)
                {
                    throw new InvalidOperationException("이 PC에는 해당 항목이 없습니다.");
                }
                foreach (var v in list)
                {
                    Reg.Write(v);
                }
                after?.Invoke();
            },
            Revert = () =>
            {
                foreach (var v in values())
                {
                    Reg.Restore(v);
                }
                afterRevert?.Invoke();
            },
        };
    }

    static Tweak Aggressive(Tweak t, string risk)
    {
        t.Aggressive = true;
        t.Risk = risk;
        return t;
    }

    // 서비스 시작 유형을 '사용 안 함(4)'으로. 서비스가 없는 PC면 키를 만들지 않도록 빈 목록 반환
    static Tweak ServiceTweak(string service, string title, string desc, string risk)
    {
        string path = $@"SYSTEM\CurrentControlSet\Services\{service}";
        return Aggressive(RegTweak(title, desc, true, () =>
        {
            using var key = RegistryKey.OpenBaseKey(HKLM, RegistryView.Registry64).OpenSubKey(path);
            if (key == null)
            {
                return new List<RegVal>();
            }
            return new List<RegVal> { new(HKLM, path, "Start", 4, DW) };
        },
        () => Native.Run("sc", $"stop {service}"),
        () => Native.Run("sc", $"start {service}")), risk);
    }

    static List<RegVal> NagleValues()
    {
        const string root = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
        var result = new List<RegVal>();
        using var key = RegistryKey.OpenBaseKey(HKLM, RegistryView.Registry64).OpenSubKey(root);
        if (key == null)
        {
            return result;
        }
        foreach (string iface in key.GetSubKeyNames())
        {
            result.Add(new RegVal(HKLM, $@"{root}\{iface}", "TcpAckFrequency", 1, DW));
            result.Add(new RegVal(HKLM, $@"{root}\{iface}", "TCPNoDelay", 1, DW));
        }
        return result;
    }

    public static List<Tweak> All()
    {
        const string mmProfile = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
        const string mmGames = mmProfile + @"\Tasks\Games";

        var list = new List<Tweak>
        {
            new Tweak
            {
                Title = "고성능 전원 관리 옵션",
                Desc = "CPU 클럭 다운/코어 파킹으로 인한 프레임 드랍 감소",
                IsApplied = Power.IsApplied,
                Apply = Power.Apply,
                Revert = Power.Revert,
            },

            RegTweak("게임 모드 켜기", "게임 프로세스 우선 처리, 백그라운드 업데이트 억제", false, () => new List<RegVal>
            {
                new(HKCU, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, DW),
                new(HKCU, @"Software\Microsoft\GameBar", "AllowAutoGameMode", 1, DW),
            }),

            RegTweak("Game DVR / 백그라운드 녹화 끄기", "녹화 버퍼로 인한 GPU 부하·스터터 제거", false, () => new List<RegVal>
            {
                new(HKCU, @"System\GameConfigStore", "GameDVR_Enabled", 0, DW),
                new(HKCU, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, DW),
            }),

            RegTweak("GPU 하드웨어 가속 스케줄링(HAGS)", "CPU→GPU 제출 지연 감소 (지원 GPU/드라이버 필요)", true, () => new List<RegVal>
            {
                new(HKLM, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, DW),
            }),

            RegTweak("마우스 가속 끄기", "포인터 정밀도 향상 해제 → 에임 일관성", false, () => new List<RegVal>
            {
                new(HKCU, @"Control Panel\Mouse", "MouseSpeed", "0", SZ),
                new(HKCU, @"Control Panel\Mouse", "MouseThreshold1", "0", SZ),
                new(HKCU, @"Control Panel\Mouse", "MouseThreshold2", "0", SZ),
            }, Native.ApplyMouseFromRegistry),

            RegTweak("Nagle 알고리즘 끄기", "작은 패킷 즉시 전송 → 온라인 게임 입력 반응 개선", true, NagleValues),

            RegTweak("멀티미디어 스케줄러 게임 우선순위", "게임 스레드에 CPU/GPU 시간 우선 배분", true, () => new List<RegVal>
            {
                new(HKLM, mmProfile, "SystemResponsiveness", 10, DW),
                new(HKLM, mmGames, "GPU Priority", 8, DW),
                new(HKLM, mmGames, "Priority", 6, DW),
                new(HKLM, mmGames, "Scheduling Category", "High", SZ),
                new(HKLM, mmGames, "SFIO Priority", "High", SZ),
            }),

            RegTweak("애니메이션·투명 효과 / 백그라운드 앱 끄기", "DWM 부하·백그라운드 CPU 점유 감소 (재로그인 후 적용)", true, () => new List<RegVal>
            {
                new(HKCU, @"Control Panel\Desktop\WindowMetrics", "MinAnimate", "0", SZ),
                new(HKCU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAnimations", 0, DW),
                new(HKCU, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0, DW),
                new(HKCU, @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1, DW),
                new(HKCU, @"Software\Microsoft\Windows\CurrentVersion\Search", "BackgroundAppGlobalToggle", 0, DW),
            }),

            // ───── 공격적 옵션 ─────

            PowerSetting.Make("코어 파킹 해제",
                "모든 CPU 코어를 항상 깨워 둠 → 코어 기상 지연으로 인한 스터터 감소",
                "• 유휴 전력·발열 증가, 노트북 배터리 수명 단축\n• 팬 소음 증가\n• 현재 전원 플랜에만 적용됨 (전원 플랜을 바꾸면 다시 적용 필요)",
                "54533251-82be-4824-96c1-47b60b740d00", "0cc5b647-c1df-4637-891a-dec35c318583", 100),

            Aggressive(RegTweak("전원 스로틀링 끄기", "백그라운드 프로세스 CPU 절전(EcoQoS) 비활성화", true, () => new List<RegVal>
            {
                new(HKLM, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1, DW),
            }), "• 노트북 배터리 사용 시간 크게 감소\n• 발열·팬 소음 증가\n• 데스크톱에선 체감 효과가 거의 없을 수 있음"),

            Aggressive(RegTweak("전역 타이머 해상도 요청 허용", "게임이 요청한 1ms 타이머를 시스템 전역에 적용 (Win11)", true, () => new List<RegVal>
            {
                new(HKLM, @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", 1, DW),
            }), "• Windows 11 / Server 2022 이상에서만 동작, Win10에선 효과 없음\n• 시스템 전체 인터럽트 증가 → 전력 소모·발열 증가\n• 최소화된 프로그램도 고해상도 타이머를 유지해 배터리 소모"),

            Boot.Make("다이나믹 틱 끄기 (bcdedit)",
                "커널 타이머 틱을 고정 → 프레임 타임 일관성 향상",
                "• BitLocker 사용 중이면 다음 부팅 시 복구 키를 요구할 수 있음 (복구 키 미리 확인 필수)\n• 유휴 전력 소모 증가, 노트북 배터리 감소\n• 일부 시스템에선 오히려 성능 저하 보고 있음",
                "disabledynamictick"),

            Aggressive(RegTweak("전체 화면 최적화(FSO) 끄기", "독점 전체 화면 동작 강제 → 일부 게임 입력 지연 감소", false, () => new List<RegVal>
            {
                new(HKCU, @"System\GameConfigStore", "GameDVR_FSEBehaviorMode", 2, DW),
                new(HKCU, @"System\GameConfigStore", "GameDVR_FSEBehavior", 2, DW),
                new(HKCU, @"System\GameConfigStore", "GameDVR_HonorUserFSEBehaviorMode", 1, DW),
                new(HKCU, @"System\GameConfigStore", "GameDVR_DXGIHonorFSEWindowsCompatible", 1, DW),
            }), "• Alt+Tab 전환이 느려지고 화면 깜빡임 발생\n• Auto HDR, 가변 재생률(VRR) 일부 기능 동작 안 할 수 있음\n• 디스코드/Steam 오버레이, 게임 바 표시 문제\n• 최신 DX12 게임은 오히려 손해일 수 있음"),

            Aggressive(RegTweak("네트워크 스로틀링 해제", "멀티미디어 재생 중 네트워크 패킷 제한(10개/ms) 해제", true, () => new List<RegVal>
            {
                new(HKLM, mmProfile, "NetworkThrottlingIndex", unchecked((int)0xffffffff), DW),
            }), "• 네트워크 인터럽트 처리로 CPU 사용률 소폭 증가\n• 오디오 재생 중 소리 끊김(DPC 지연)이 생길 수 있음"),

            Aggressive(RegTweak("메모리 무결성(HVCI) 끄기", "가상화 기반 코드 무결성 해제 → 일부 CPU에서 5~10% 성능 향상", true, () => new List<RegVal>
            {
                new(HKLM, @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", 0, DW),
            }), "• 보안 약화: 커널 드라이버 공격·악성 드라이버 로드 방어가 꺼짐\n• 일부 안티치트(예: Vanguard, FACEIT)가 켜져 있기를 요구할 수 있음\n• 회사/학교 관리 PC에선 정책으로 다시 켜질 수 있음"),

            ServiceTweak("SysMain", "SysMain(슈퍼패치) 서비스 끄기", "프리로딩 디스크/메모리 사용 제거",
                "• 자주 쓰는 앱 실행 속도 느려짐 (특히 HDD)\n• 게임 로딩이 오히려 길어질 수 있음\n• SSD 환경에서만 권장"),

            ServiceTweak("WSearch", "Windows Search 인덱싱 서비스 끄기", "백그라운드 파일 인덱싱으로 인한 디스크·CPU 사용 제거",
                "• 시작 메뉴·탐색기 파일 검색이 매우 느려지거나 결과가 안 나옴\n• Outlook 메일 검색이 동작하지 않음"),

            ServiceTweak("DiagTrack", "원격 분석(DiagTrack) 서비스 끄기", "텔레메트리 수집·전송 중단",
                "• 성능 효과는 미미함\n• 일부 Windows 업데이트/피드백 허브 기능 동작 안 함\n• 기업 관리 PC에선 정책 위반일 수 있음"),
        };

        // ───── 자동 최적화 규칙: null = 추천, 문자열 = 제외 사유 (규칙 없는 항목은 항상 추천) ─────
        const string laptop = "노트북 — 배터리·발열 손해가 이득보다 큼";

        Auto(list, "고성능 전원 관리 옵션", e =>
        {
            if (e.IsLaptop == true && e.OnAC == false)
            {
                return "노트북 배터리 사용 중 — 충전기 연결 후 다시 실행하세요";
            }
            return null;
        });
        Auto(list, "GPU 하드웨어 가속 스케줄링(HAGS)", e =>
        {
            if (e.Build < 19041)
            {
                return "Windows 10 2004 미만은 지원하지 않음";
            }
            return null;
        });
        Auto(list, "코어 파킹 해제", e =>
        {
            if (e.IsLaptop == true)
            {
                return laptop;
            }
            return null;
        });
        Auto(list, "전원 스로틀링 끄기", e =>
        {
            if (e.IsLaptop == true)
            {
                return laptop;
            }
            return null;
        });
        Auto(list, "전역 타이머 해상도 요청 허용", e =>
        {
            if (e.IsWin11 == false)
            {
                return "Windows 11 전용 기능";
            }
            if (e.IsLaptop == true)
            {
                return laptop;
            }
            return null;
        });
        Auto(list, "다이나믹 틱 끄기 (bcdedit)", e =>
        {
            if (e.BitLocker == true)
            {
                return "BitLocker 켜짐 — 부팅 시 복구 키 요구 위험";
            }
            return "시스템마다 효과가 갈림 — 수동으로 테스트 후 선택 권장";
        });
        Auto(list, "전체 화면 최적화(FSO) 끄기", _ => "최신 DX12 게임은 손해일 수 있음 — 특정 게임에서만 수동 선택 권장");
        Auto(list, "네트워크 스로틀링 해제", _ => "체감 효과가 작고 오디오 끊김 위험");
        Auto(list, "메모리 무결성(HVCI) 끄기", e =>
        {
            if (e.HasStrictAntiCheat == true)
            {
                return "Vanguard/FACEIT 안티치트 감지 — 켜져 있어야 할 수 있음";
            }
            return "보안이 약해짐 — 자동으로는 끄지 않음 (수동 선택)";
        });
        Auto(list, "SysMain(슈퍼패치) 서비스 끄기", e =>
        {
            if (e.SystemDisk != "SSD")
            {
                return $"시스템 드라이브가 SSD가 아님 ({e.SystemDisk})";
            }
            return null;
        });
        Auto(list, "Windows Search 인덱싱 서비스 끄기", _ => "검색 기능을 잃는 손해가 큼 — 수동 선택");
        Auto(list, "원격 분석(DiagTrack) 서비스 끄기", _ => "성능 효과 미미");
        return list;
    }

    static void Auto(List<Tweak> list, string title, Func<SysEnv, string?> rule)
    {
        var t = list.Find(x => x.Title == title);
        if (t == null)
        {
            throw new InvalidOperationException($"자동 규칙 대상 없음: {title}");
        }
        t.AutoSkip = rule;
    }
}

// ───────────────────────── UI ─────────────────────────

class MainForm : Form
{
    readonly List<Tweak> tweaks = Tweaks.All();
    readonly ListView list;
    readonly TextBox log;
    readonly FlowLayoutPanel buttons;
    readonly ToolTip tip = new ToolTip { AutoPopDelay = 30000, InitialDelay = 300, ReshowDelay = 100 };
    ListViewItem? hoverItem;

    public MainForm()
    {
        Text = $"GameTuner v{Updater.Current} - 게임용 윈도우 최적화";
        Size = new Size(1000, 640);
        StartPosition = FormStartPosition.CenterScreen;
        // EXE에 박힌 app.ico를 창 제목줄/작업 표시줄에도 사용
        if (Environment.ProcessPath != null)
        {
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath);
        }

        list = new ListView
        {
            View = View.Details,
            CheckBoxes = true,
            FullRowSelect = true,
            Dock = DockStyle.Fill,
        };
        list.Columns.Add("항목", 260);
        list.Columns.Add("구분", 70);
        list.Columns.Add("상태", 80);
        list.Columns.Add("재부팅", 60);
        list.Columns.Add("설명", 480);
        list.MouseMove += OnListMouseMove;
        list.MouseLeave += (_, _) =>
        {
            hoverItem = null;
            tip.Hide(list);
        };

        log = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Bottom,
            Height = 140,
        };

        buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(4) };
        AddButton("복원 지점 만들기", async (_, _) => await CreateRestorePoint());
        AddButton("★ 자동 최적화", async (_, _) => await AutoOptimize());
        AddButton("PC 사양", async (_, _) => await ShowSpecs());
        AddButton("안전 항목 전체 선택", (_, _) => SetAllChecked(true));
        AddButton("선택 해제", (_, _) => SetAllChecked(false));
        AddButton("선택 적용", (_, _) => Run(true));
        AddButton("선택 되돌리기", (_, _) => Run(false));
        AddButton("새로고침", (_, _) => RefreshList());
        AddButton("업데이트 확인", async (_, _) => await CheckUpdate(false));

        Controls.Add(list);
        Controls.Add(log);
        Controls.Add(buttons);

        RefreshList();
        Log($"백업 파일: {Backup.FilePath}");
        Shown += async (_, _) => await CheckUpdate(true);
    }

    async Task ShowSpecs()
    {
        Log("PC 사양 조회 중...");
        UseWaitCursor = true;
        string specs = await Task.Run(Specs.Query);
        UseWaitCursor = false;
        foreach (string line in specs.Split('\n'))
        {
            Log(line.TrimEnd());
        }
        // 메시지 창에서 Ctrl+C로 전체 복사 가능
        MessageBox.Show(this, specs, "PC 사양", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // silent = 시작 시 자동 확인: 새 버전 없음/네트워크 오류는 로그만 남기고 조용히 넘어감
    async Task CheckUpdate(bool silent)
    {
        (Version Latest, string Url)? update;
        try
        {
            update = await Updater.CheckAsync();
        }
        catch (Exception ex)
        {
            Log($"업데이트 확인 실패: {ex.Message}");
            if (silent == false)
            {
                MessageBox.Show(this, $"업데이트 확인 실패: {ex.Message}", "업데이트", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return;
        }

        if (update == null)
        {
            Log($"최신 버전입니다. (v{Updater.Current})");
            if (silent == false)
            {
                MessageBox.Show(this, $"최신 버전입니다. (v{Updater.Current})", "업데이트", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return;
        }

        Log($"새 버전 발견: v{update.Value.Latest}");
        var answer = MessageBox.Show(this,
            $"새 버전 v{update.Value.Latest}이 있습니다. (현재 v{Updater.Current})\n지금 업데이트하고 다시 시작할까요?",
            "업데이트", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        try
        {
            Log("다운로드 중...");
            UseWaitCursor = true;
            await Updater.InstallAsync(update.Value.Url);
        }
        catch (Exception ex)
        {
            UseWaitCursor = false;
            Log($"업데이트 실패: {ex.Message}");
            MessageBox.Show(this, $"업데이트 실패: {ex.Message}", "업데이트", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void AddButton(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += onClick;
        buttons.Controls.Add(b);
    }

    // 전체 선택은 안전 항목만 — 공격적 옵션은 하나씩 직접 체크하도록
    void SetAllChecked(bool value)
    {
        foreach (ListViewItem item in list.Items)
        {
            if (value == true && tweaks[item.Index].Aggressive == true)
            {
                continue;
            }
            item.Checked = value;
        }
    }

    void OnListMouseMove(object? sender, MouseEventArgs e)
    {
        var item = list.GetItemAt(e.X, e.Y);
        if (item == hoverItem)
        {
            return;
        }
        hoverItem = item;
        if (item == null)
        {
            tip.Hide(list);
            return;
        }

        var t = tweaks[item.Index];
        if (t.Aggressive == true)
        {
            tip.ToolTipIcon = ToolTipIcon.Warning;
            tip.ToolTipTitle = $"⚠ {t.Title} — 발생할 수 있는 문제";
            tip.Show(t.Risk, list, e.X + 16, e.Y + 16, tip.AutoPopDelay);
        }
        else
        {
            tip.ToolTipIcon = ToolTipIcon.Info;
            tip.ToolTipTitle = t.Title;
            tip.Show(t.Desc, list, e.X + 16, e.Y + 16, tip.AutoPopDelay);
        }
    }

    // 현재 시스템 상태 기준으로 다시 그림 — 이미 적용된 항목(직접 설정한 것 포함)은 체크됨
    void RefreshList()
    {
        list.BeginUpdate();
        list.Items.Clear();
        for (int i = 0; i < tweaks.Count; i++)
        {
            var t = tweaks[i];
            string status;
            bool applied = false;
            try
            {
                applied = t.IsApplied();
                if (applied == true)
                {
                    status = "적용됨";
                }
                else
                {
                    status = "기본값";
                }
            }
            catch (Exception ex)
            {
                status = "확인 불가";
                Log($"[{t.Title}] 상태 확인 실패: {ex.Message}");
            }

            string reboot;
            if (t.NeedsReboot == true)
            {
                reboot = "필요";
            }
            else
            {
                reboot = "";
            }

            string kind;
            if (t.Aggressive == true)
            {
                kind = "⚠ 공격적";
            }
            else
            {
                kind = "안전";
            }

            var item = new ListViewItem(new[] { t.Title, kind, status, reboot, t.Desc });
            item.Checked = applied;
            if (t.Aggressive == true)
            {
                item.ForeColor = Color.Firebrick;
            }
            list.Items.Add(item);
        }
        list.EndUpdate();
    }

    void Run(bool apply, bool skipConfirm = false)
    {
        if (list.CheckedItems.Count == 0)
        {
            Log("선택된 항목이 없습니다.");
            return;
        }

        if (apply == true && skipConfirm == false && ConfirmAggressive() == false)
        {
            Log("공격적 옵션 적용을 취소했습니다.");
            return;
        }

        bool needReboot = false;
        Cursor = Cursors.WaitCursor;
        foreach (ListViewItem item in list.CheckedItems)
        {
            var t = tweaks[item.Index];
            try
            {
                if (apply == true)
                {
                    if (t.IsApplied() == true)
                    {
                        Log($"[건너뜀] {t.Title}: 이미 적용되어 있음");
                        continue;
                    }
                    t.Apply();
                    Log($"[적용] {t.Title}");
                }
                else
                {
                    t.Revert();
                    if (t.IsApplied() == true)
                    {
                        // 백업이 없음 = 이 프로그램 이전에 직접 설정한 값 → 원래 값을 모르므로 건드리지 않음
                        Log($"[유지] {t.Title}: 직접 설정한 값이라 되돌릴 백업이 없음");
                        continue;
                    }
                    Log($"[되돌림] {t.Title}");
                }
                if (t.NeedsReboot == true)
                {
                    needReboot = true;
                }
            }
            catch (Exception ex)
            {
                Log($"[실패] {t.Title}: {ex.Message}");
            }
        }
        Cursor = Cursors.Default;

        RefreshList();
        if (needReboot == true)
        {
            Log("※ 일부 항목은 재부팅(또는 재로그인) 후 완전히 반영됩니다.");
            MessageBox.Show(this, "일부 항목은 재부팅 후 완전히 반영됩니다.", "GameTuner", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    // 아직 적용 안 된 공격적 옵션이 선택돼 있으면 위험 내용을 모아 한 번 더 확인
    bool ConfirmAggressive()
    {
        var sb = new System.Text.StringBuilder();
        foreach (ListViewItem item in list.CheckedItems)
        {
            var t = tweaks[item.Index];
            if (t.Aggressive == false || t.IsApplied() == true)
            {
                continue;
            }
            sb.AppendLine($"■ {t.Title}");
            sb.AppendLine(t.Risk);
            sb.AppendLine();
        }
        if (sb.Length == 0)
        {
            return true;
        }
        sb.AppendLine("복원 지점을 먼저 만드는 것을 권장합니다. 계속할까요?");
        var answer = MessageBox.Show(this, sb.ToString(), "공격적 옵션 적용 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        return answer == DialogResult.Yes;
    }

    // PC 환경을 감지해 효과 있는 항목만 체크 → 확인 후 적용
    async Task AutoOptimize()
    {
        Log("PC 환경 감지 중...");
        UseWaitCursor = true;
        var env = await Task.Run(SysEnv.Detect);
        UseWaitCursor = false;
        Log($"감지 결과: {env}");

        var picked = new List<Tweak>();
        var summary = new System.Text.StringBuilder();
        summary.AppendLine($"감지된 환경: {env}");
        summary.AppendLine();
        summary.AppendLine("적용할 항목:");

        foreach (ListViewItem item in list.Items)
        {
            var t = tweaks[item.Index];
            if (t.IsApplied() == true)
            {
                item.Checked = true;
                continue;
            }

            string? skip = t.AutoSkip(env);
            if (skip != null)
            {
                item.Checked = false;
                Log($"[제외] {t.Title}: {skip}");
                continue;
            }

            item.Checked = true;
            picked.Add(t);
            if (t.Aggressive == true)
            {
                summary.AppendLine($"  ⚠ {t.Title}");
                foreach (string riskLine in t.Risk.Split('\n'))
                {
                    summary.AppendLine($"      {riskLine}");
                }
            }
            else
            {
                summary.AppendLine($"  • {t.Title}");
            }
        }

        if (picked.Count == 0)
        {
            Log("추천 항목이 모두 이미 적용되어 있습니다.");
            MessageBox.Show(this, "이 PC에 추천하는 항목은 이미 모두 적용되어 있습니다.", "자동 최적화", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        summary.AppendLine();
        summary.AppendLine("제외된 항목과 이유는 아래 로그 창에서 볼 수 있습니다.");
        summary.AppendLine("복원 지점을 먼저 만드는 것을 권장합니다. 적용할까요?");
        var answer = MessageBox.Show(this, summary.ToString(), $"자동 최적화 — {picked.Count}개 항목", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            Log("자동 최적화를 취소했습니다. (추천 항목은 체크된 상태로 남아 있음)");
            return;
        }
        Run(true, skipConfirm: true);
    }

    async Task CreateRestorePoint()
    {
        Log("복원 지점 생성 중... (최대 1~2분)");
        UseWaitCursor = true;
        var result = await Task.Run(() => Native.Run("powershell",
            "-NoProfile -ExecutionPolicy Bypass -Command \"Checkpoint-Computer -Description 'GameTuner' -RestorePointType MODIFY_SETTINGS\""));
        UseWaitCursor = false;

        if (result.Code == 0)
        {
            Log("복원 지점 생성 완료.");
        }
        else
        {
            // 시스템 보호가 꺼져 있거나 24시간 내 이미 생성된 경우
            Log($"복원 지점 생성 실패: {result.Output.Trim()}");
        }
    }

    void Log(string message)
    {
        log.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
    }
}

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;

// ── Версия ────────────────────────────────────────────────────────────────────
static class App
{
    public const string Version = "1.0.0.0";
}

// ── Локализация ───────────────────────────────────────────────────────────────
static class L
{
    public static bool IsEn { get; private set; } = true;
    public static void SetEn(bool en) => IsEn = en;
    static string S(string en, string ru) => IsEn ? en : ru;

    public static string FieldIp       => S("Server IP",           "IP сервера");
    public static string FieldPort     => S("Port",                "Порт");
    public static string FieldPassword => S("Password",            "Пароль");
    public static string Remember      => S("Remember credentials", "Запомнить данные");
    public static string BtnConnect    => S("Connect",             "Подключиться");
    public static string BtnConnecting => S("Connecting…",         "Подключение…");
    public static string BtnDisconnect => S("Disconnect",          "Отключиться");
    public static string BtnSend       => S("Send",                "Отправить");
    public static string StatusConn    => S("Connected",           "Подключено");
    public static string StatusDisc    => S("Disconnected",        "Отключено");
    public static string ErrBadPort    => S("Invalid port",        "Неверный порт");
    public static string ErrNoResp     => S("No response — RCon may be disabled or address is wrong",
                                            "Нет ответа — RCon отключён или неверный адрес");
    public static string ErrBadPwd     => S("Wrong password",      "Неверный пароль");
    public static string ErrNoHost     => S("Could not resolve host", "Не удалось определить адрес");
    public static string ErrLost       => S("Connection lost",     "Соединение потеряно");
    public static string WarnLost      => S("[Warning] Connection to server lost",
                                            "[Warning] Соединение с сервером потеряно");
    public static string InfoConnected => S("[Info] Connected successfully",
                                            "[Info] Успешное подключение");
    public static string TypeCmd       => S("Type a command…",     "Введите команду…");
    public static string LangLabel     => S("Language",            "Язык");
}

// ── Цвета ─────────────────────────────────────────────────────────────────────
static class Clr
{
    public static readonly Color Bg        = Color.FromArgb(13,  13,  20);
    public static readonly Color Card      = Color.FromArgb(22,  22,  34);
    public static readonly Color CardEdge  = Color.FromArgb(40,  40,  62);
    public static readonly Color Input     = Color.FromArgb(16,  16,  26);
    public static readonly Color InputBord = Color.FromArgb(52,  52,  78);
    public static readonly Color InputFoc  = Color.FromArgb(99, 141, 221);
    public static readonly Color Accent    = Color.FromArgb(88, 132, 216);
    public static readonly Color AccentHov = Color.FromArgb(108,152,236);
    public static readonly Color Fg        = Color.FromArgb(210, 215, 240);
    public static readonly Color FgDim     = Color.FromArgb(100, 105, 135);
    public static readonly Color Green     = Color.FromArgb(120, 200, 120);
    public static readonly Color Red       = Color.FromArgb(220,  90, 100);
    public static readonly Color Yellow    = Color.FromArgb(230, 190, 100);
    public static readonly Color Blue      = Color.FromArgb(120, 165, 235);
    public static readonly Color LogBg     = Color.FromArgb(10,  10,  16);
    public static readonly Color TopBar    = Color.FromArgb(18,  18,  28);
    public static readonly Color LinkColor = Color.FromArgb(99, 141, 221);
    public static readonly Color TimeColor = Color.FromArgb(65,  68,  95);
}

// ── Конфиг (%APPDATA%\RconClient\config.json) ────────────────────────────────
class Config
{
    public string Ip       { get; set; } = "127.0.0.1";
    public int    Port     { get; set; } = 2305;
    public string Password { get; set; } = "";
    public bool   Remember { get; set; } = false;
    public string Language { get; set; } = "en";

    static string Dir     => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RconClient");
    static string CfgPath => System.IO.Path.Combine(Dir, "config.json");

    public static Config Load()
    {
        try
        {
            if (File.Exists(CfgPath))
            {
                var c = JsonSerializer.Deserialize<Config>(File.ReadAllText(CfgPath));
                if (c != null) return c;
            }
        }
        catch { }
        return new Config();
    }

    // Сохраняем только если Remember = true, иначе удаляем файл
    public void Save()
    {
        if (!Remember)
        {
            try { if (File.Exists(CfgPath)) File.Delete(CfgPath); } catch { }
            return;
        }
        Directory.CreateDirectory(Dir);
        File.WriteAllText(CfgPath, JsonSerializer.Serialize(this,
            new JsonSerializerOptions { WriteIndented = true }));
    }
}

// ── RCon-клиент ───────────────────────────────────────────────────────────────
class RconClient : IDisposable
{
    UdpClient?  _udp;
    IPEndPoint? _ep;
    byte        _seq;
    DateTime    _lastSent    = DateTime.MinValue;
    DateTime    _connectTime;
    volatile bool _connecting;
    volatile bool _running;

    readonly Dictionary<byte, (byte total, Dictionary<byte, string> parts)> _multi = [];
    readonly Queue<string> _msgs = [];
    readonly object        _lock = new();
    Thread? _thread;

    public bool IsConnected  { get; private set; }
    public bool IsConnecting => _connecting;

    static readonly Encoding ENC = new UTF8Encoding(false);

    static uint Crc32(byte[] d, int off, int len)
    {
        uint c = 0xFFFF_FFFFu;
        for (int i = off; i < off + len; i++)
        { c ^= d[i]; for (int b = 0; b < 8; b++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB8_8320u : c >> 1; }
        return ~c;
    }

    static byte[] BuildPacket(byte[] payload)
    {
        var h = new byte[1 + payload.Length]; h[0] = 0xFF; payload.CopyTo(h, 1);
        uint crc = Crc32(h, 0, h.Length);
        var pkt = new byte[7 + payload.Length];
        pkt[0] = 0x42; pkt[1] = 0x45;
        pkt[2] = (byte)crc; pkt[3] = (byte)(crc >> 8);
        pkt[4] = (byte)(crc >> 16); pkt[5] = (byte)(crc >> 24);
        pkt[6] = 0xFF; payload.CopyTo(pkt, 7);
        return pkt;
    }

    void Send(byte[] payload)
    {
        if (_udp is null) return;
        var p = BuildPacket(payload);
        _udp.Send(p, p.Length);
        _lastSent = DateTime.UtcNow;
    }

    public void Connect(string host, int port, string password)
    {
        Disconnect();
        _connecting = true; _seq = 0; _connectTime = DateTime.UtcNow; _multi.Clear();
        try
        {
            var addrs = Dns.GetHostAddresses(host);
            if (addrs.Length == 0) throw new Exception(L.ErrNoHost);
            _ep  = new IPEndPoint(addrs[0], port);
            _udp = new UdpClient();
            _udp.Client.ReceiveTimeout = 100;
            _udp.Connect(_ep);
        }
        catch (Exception ex) { Push("[Error] " + ex.Message); _connecting = false; return; }

        var pwd = ENC.GetBytes(password);
        var pay = new byte[1 + pwd.Length]; pay[0] = 0x00; pwd.CopyTo(pay, 1);
        Send(pay);
        _running = true;
        _thread  = new Thread(Loop) { IsBackground = true, Name = "RCon" };
        _thread.Start();
    }

    public void Disconnect()
    {
        _running = false; IsConnected = false; _connecting = false;
        _thread?.Join(300); _thread = null;
        try { _udp?.Close(); } catch { }
        _udp = null; _multi.Clear();
    }

    public void SendCommand(string cmd)
    {
        if (!IsConnected) return;
        var t = ENC.GetBytes(cmd);
        var p = new byte[2 + t.Length]; p[0] = 0x01; p[1] = _seq++; t.CopyTo(p, 2);
        Send(p);
    }

    void Ack(byte seq) => Send([0x02, seq]);

    void Loop()
    {
        while (_running)
        {
            if (IsConnected && (DateTime.UtcNow - _lastSent).TotalSeconds > 30)
                Send([0x01, _seq++]);
            if (_connecting && !IsConnected && (DateTime.UtcNow - _connectTime).TotalSeconds > 5)
            { Push("[Error] " + L.ErrNoResp); _connecting = false; _running = false; return; }
            try
            {
                var ep = _ep!;
                Handle(_udp!.Receive(ref ep));
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut) { }
            catch
            {
                if (_running && IsConnected) Push("[Error] " + L.ErrLost);
                IsConnected = false; _running = false; return;
            }
        }
    }

    void Handle(byte[] d)
    {
        if (d.Length < 8 || d[0] != 0x42 || d[1] != 0x45 || d[6] != 0xFF) return;
        uint stored = d[2] | ((uint)d[3] << 8) | ((uint)d[4] << 16) | ((uint)d[5] << 24);
        if (Crc32(d, 6, d.Length - 6) != stored) return;
        byte type = d[7];

        if (type == 0x00 && _connecting && d.Length >= 9)
        {
            if (d[8] == 0x01) { IsConnected = true; _connecting = false; Push(L.InfoConnected); }
            else { Push("[Error] " + L.ErrBadPwd); _connecting = false; _running = false; }
            return;
        }
        if (type == 0x01 && d.Length >= 9)
        {
            byte seq = d[8];
            if (d.Length >= 12 && d[9] == 0x00)
            {
                byte total = d[10], idx = d[11];
                if (!_multi.ContainsKey(seq)) _multi[seq] = (total, []);
                _multi[seq].parts[idx] = ENC.GetString(d, 12, d.Length - 12);
                if (_multi[seq].parts.Count == _multi[seq].total)
                {
                    var sb = new StringBuilder();
                    for (byte i = 0; i < _multi[seq].total; i++) sb.Append(_multi[seq].parts[i]);
                    _multi.Remove(seq);
                    if (sb.Length > 0) Push(sb.ToString());
                }
            }
            else if (d.Length > 9) { var m = ENC.GetString(d, 9, d.Length - 9); if (m.Length > 0) Push(m); }
            return;
        }
        if (type == 0x02 && d.Length >= 9)
        {
            Ack(d[8]);
            if (d.Length > 9) { var m = ENC.GetString(d, 9, d.Length - 9); if (m.Length > 0) Push(m); }
        }
    }

    void Push(string m) { lock (_lock) _msgs.Enqueue(m); }
    public string? Poll() { lock (_lock) return _msgs.Count > 0 ? _msgs.Dequeue() : null; }
    public void Dispose() => Disconnect();
}

// ── Поле ввода в стиле Material ───────────────────────────────────────────────
class FloatField : UserControl
{
    readonly Label   _lbl = new();
    readonly TextBox _tb  = new();
    bool _focused;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string LabelText { get => _lbl.Text; set => _lbl.Text = value; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Value     { get => _tb.Text;  set => _tb.Text  = value; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsPassword  { set => _tb.UseSystemPasswordChar = value; }

    public new event KeyEventHandler? KeyDown;

    public FloatField()
    {
        Height    = 54;
        BackColor = Clr.Card;

        _lbl.ForeColor = Clr.FgDim;
        _lbl.Font      = new Font("Segoe UI", 8.5f);
        _lbl.AutoSize  = true;
        _lbl.Location  = new Point(0, 2);

        _tb.BorderStyle = BorderStyle.None;
        _tb.BackColor   = Clr.Card;
        _tb.ForeColor   = Clr.Fg;
        _tb.Font        = new Font("Segoe UI", 11f);
        _tb.Location    = new Point(0, 26);

        _tb.GotFocus  += (_, _) => { _focused = true;  Invalidate(); };
        _tb.LostFocus += (_, _) => { _focused = false; Invalidate(); };
        _tb.KeyDown   += (s, e) => KeyDown?.Invoke(s, e);

        Controls.Add(_lbl);
        Controls.Add(_tb);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _tb.Width = Width;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var pen = _focused
            ? new Pen(Clr.InputFoc, 2)
            : new Pen(Clr.InputBord, 1);
        int y = Height - 2;
        e.Graphics.DrawLine(pen, 0, y, Width, y);
        pen.Dispose();
    }

    protected override void SetBoundsCore(int x, int y, int w, int h, BoundsSpecified s)
        => base.SetBoundsCore(x, y, w, 54, s);
}

// ── Кликабельная ссылка ───────────────────────────────────────────────────────
class LinkLabel2 : Label
{
    readonly string _url;
    bool _hovered;

    public LinkLabel2(string text, string url) : base()
    {
        _url      = url;
        Text      = text;
        ForeColor = Clr.FgDim;
        Font      = new Font("Segoe UI", 8.5f);
        AutoSize  = true;
        Cursor    = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered  = true;
        ForeColor = Clr.LinkColor;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered  = false;
        ForeColor = Clr.FgDim;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnClick(EventArgs e)
    {
        Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true });
        base.OnClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_hovered)
        {
            using var pen = new Pen(Clr.LinkColor, 1);
            e.Graphics.DrawLine(pen, 0, Height - 2, Width, Height - 2);
        }
    }
}

// ── Главная форма ─────────────────────────────────────────────────────────────
class MainForm : Form
{
    // Логин
    Panel      _loginPanel  = new();
    FloatField _fIp         = new();
    FloatField _fPort       = new();
    FloatField _fPwd        = new();
    CheckBox   _chkRemember = new();
    Button     _btnConnect  = new();
    Label      _lblError    = new();

    // Консоль
    Panel       _conPanel   = new();
    RichTextBox _log        = new();
    TextBox     _tbCmd      = new();
    Button      _btnSend    = new();
    Button      _btnDisc    = new();
    Label       _lblStatus  = new();
    Label       _lblServer  = new();

    // Общие
    ComboBox   _cmbLang     = new();
    Label      _lblVersion  = new();

    readonly RconClient _rcon = new();
    readonly Config     _cfg  = Config.Load();
    bool   _wasConnected;
    string _connectedTo = "";

    public MainForm()
    {
        Text          = "DayZ RCon Client";
        MinimumSize   = new Size(800, 600);
        Size          = new Size(1040, 720);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor     = Clr.Bg;
        Font          = new Font("Segoe UI", 10f);

        L.SetEn(_cfg.Language != "ru");

        BuildVersionLabel();
        BuildLangDropdown();
        BuildLoginPanel();
        BuildConsolePanel();
        ShowLogin();

        var timer = new System.Windows.Forms.Timer { Interval = 50 };
        timer.Tick += Poll;
        timer.Start();
        FormClosing += (_, _) => _rcon.Dispose();
    }

    // ── Версия (левый нижний угол) ────────────────────────────────────────────

    void BuildVersionLabel()
    {
        _lblVersion = new Label
        {
            Text      = "v" + App.Version,
            ForeColor = Clr.FgDim,
            BackColor = Color.Transparent,
            Font      = new Font("Segoe UI", 8f),
            AutoSize  = true,
            Anchor    = AnchorStyles.Bottom | AnchorStyles.Left,
        };

        void Repos() => _lblVersion.Location =
            new Point(10, ClientSize.Height - _lblVersion.Height - 6);

        Resize += (_, _) => Repos();

        Controls.Add(_lblVersion);
        _lblVersion.BringToFront();
    }

    // ── Дропдаун языка (правый верхний угол) ─────────────────────────────────

    void BuildLangDropdown()
    {
        _cmbLang = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Size          = new Size(68, 26),
            Anchor        = AnchorStyles.Top | AnchorStyles.Right,
            BackColor     = Clr.Card,
            ForeColor     = Clr.FgDim,
            FlatStyle     = FlatStyle.Flat,
            Font          = new Font("Segoe UI", 9f),
        };
        _cmbLang.Items.Add("EN");
        _cmbLang.Items.Add("RU");
        _cmbLang.SelectedIndex = L.IsEn ? 0 : 1;

        void Repos() => _cmbLang.Location = new Point(ClientSize.Width - 80, 10);
        Repos();
        Resize += (_, _) => Repos();

        _cmbLang.SelectedIndexChanged += (_, _) =>
        {
            L.SetEn(_cmbLang.SelectedIndex == 0);
            _cfg.Language = L.IsEn ? "en" : "ru";
            if (_cfg.Remember) _cfg.Save();
            ApplyLang();
        };

        Controls.Add(_cmbLang);
        _cmbLang.BringToFront();
    }

    void ApplyLang()
    {
        _fIp.LabelText   = L.FieldIp;
        _fPort.LabelText  = L.FieldPort;
        _fPwd.LabelText   = L.FieldPassword;
        _chkRemember.Text = L.Remember;
        _btnConnect.Text  = _btnConnect.Enabled ? L.BtnConnect : L.BtnConnecting;
        _btnDisc.Text     = L.BtnDisconnect;
        _btnSend.Text     = L.BtnSend;
        _tbCmd.PlaceholderText = L.TypeCmd;
        bool conn = _rcon.IsConnected;
        UpdateStatusLabel(conn);
    }

    void UpdateStatusLabel(bool connected)
    {
        if (connected)
        {
            _lblStatus.Text      = "● " + L.StatusConn;
            _lblStatus.ForeColor = Clr.Green;
            _lblServer.Text      = _connectedTo;
        }
        else
        {
            _lblStatus.Text      = "● " + L.StatusDisc;
            _lblStatus.ForeColor = Clr.Red;
            _lblServer.Text      = "";
        }
    }

    // ── Экран входа ───────────────────────────────────────────────────────────

    void BuildLoginPanel()
    {
        _loginPanel = new Panel { Dock = DockStyle.Fill, BackColor = Clr.Bg };

        var card = new Panel { Size = new Size(420, 440), BackColor = Clr.Card };
        card.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Clr.CardEdge);
            e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
        };

        // Шапка карточки
        var header = new Panel
        {
            Location  = new Point(0, 0),
            Size      = new Size(420, 96),
            BackColor = Color.FromArgb(17, 17, 29),
        };
        header.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Иконка-щит
            int cx = 210, cy = 38, r = 22;
            var shield = new Rectangle(cx - r, cy - r, r * 2, r * 2 + 6);
            using var fill = new SolidBrush(Clr.Accent);
            var path = new GraphicsPath();
            path.AddArc(shield.X,         shield.Y, 10, 10, 180, 90);
            path.AddArc(shield.Right - 10, shield.Y, 10, 10, 270, 90);
            path.AddLine(shield.Right, shield.Y + 5, shield.Right, cy + 8);
            path.AddBezier(shield.Right, cy + 8, shield.Right, shield.Bottom,
                           cx, shield.Bottom, cx, shield.Bottom);
            path.AddBezier(cx, shield.Bottom, shield.X, shield.Bottom,
                           shield.X, cy + 8, shield.X, cy + 8);
            path.CloseFigure();
            g.FillPath(fill, path);

            using var wp = new Pen(Color.White, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(wp, new[] { new PointF(cx - 8, cy + 2), new PointF(cx - 2, cy + 8), new PointF(cx + 9, cy - 6) });
            path.Dispose();
        };

        var lblTitle = new Label
        {
            Text      = "DayZ RCon Client",
            Location  = new Point(0, 68),
            Size      = new Size(420, 22),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Clr.Fg,
            Font      = new Font("Segoe UI", 10f, FontStyle.Bold),
            BackColor = Color.Transparent,
        };
        header.Controls.Add(lblTitle);
        card.Controls.Add(header);

        // Поля
        int px = 36, py = 110, gap = 62;

        _fIp  = MkField(L.FieldIp,       px, py,          _cfg.Ip,               false);
        _fPort = MkField(L.FieldPort,    px, py + gap,     _cfg.Port.ToString(),  false);
        _fPwd = MkField(L.FieldPassword, px, py + gap * 2, _cfg.Remember ? _cfg.Password : "", true);
        _fPwd.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) DoConnect(null, e); };

        card.Controls.Add(_fIp);
        card.Controls.Add(_fPort);
        card.Controls.Add(_fPwd);

        // Галочка
        _chkRemember = new CheckBox
        {
            Text      = L.Remember,
            Location  = new Point(px, py + gap * 3 + 6),
            AutoSize  = true,
            ForeColor = Clr.FgDim,
            BackColor = Clr.Card,
            Font      = new Font("Segoe UI", 9.5f),
            Checked   = _cfg.Remember,
            Cursor    = Cursors.Hand,
        };
        card.Controls.Add(_chkRemember);

        // Кнопка
        _btnConnect = MkAccentBtn(L.BtnConnect, px, py + gap * 3 + 38, 348, 42);
        _btnConnect.Click += DoConnect;
        card.Controls.Add(_btnConnect);

        // Ошибка
        _lblError = new Label
        {
            Location  = new Point(px, py + gap * 3 + 90),
            Size      = new Size(348, 32),
            ForeColor = Clr.Red,
            BackColor = Clr.Card,
            Font      = new Font("Segoe UI", 9f),
            TextAlign = ContentAlignment.TopCenter,
        };
        card.Controls.Add(_lblError);

        // Водяной знак — ссылки внизу карточки
        var lnkSite = new LinkLabel2("unpleme.com", "https://unpleme.com");
        var lnkGh   = new LinkLabel2("github.com/unpleme", "https://github.com/unpleme");
        var sep     = new Label
        {
            Text = "·", ForeColor = Clr.FgDim,
            Font = new Font("Segoe UI", 8.5f), AutoSize = true,
        };

        // Выравниваем по центру снизу карточки
        card.Controls.Add(lnkSite);
        card.Controls.Add(sep);
        card.Controls.Add(lnkGh);

        card.Resize += (_, _) => PositionLinks();
        void PositionLinks()
        {
            int totalW = lnkSite.Width + sep.Width + lnkGh.Width + 12;
            int startX = (card.Width - totalW) / 2;
            int y      = card.Height - 22;
            lnkSite.Location = new Point(startX, y);
            sep.Location     = new Point(startX + lnkSite.Width + 4, y + 1);
            lnkGh.Location   = new Point(startX + lnkSite.Width + sep.Width + 8, y);
        }

        _loginPanel.Resize += (_, _) =>
        {
            card.Location = new Point(
                (_loginPanel.Width  - card.Width)  / 2,
                (_loginPanel.Height - card.Height) / 2);
            PositionLinks();
        };

        _loginPanel.Controls.Add(card);
        Controls.Add(_loginPanel);
    }

    FloatField MkField(string label, int x, int y, string val, bool pwd)
        => new FloatField { Location = new Point(x, y), Width = 348, LabelText = label, Value = val, IsPassword = pwd };

    Button MkAccentBtn(string text, int x, int y, int w, int h)
    {
        var b = new Button
        {
            Text = text, Location = new Point(x, y), Size = new Size(w, h),
            BackColor = Clr.Accent, ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font      = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            Cursor    = Cursors.Hand,
        };
        b.FlatAppearance.BorderSize = 0;
        b.MouseEnter += (_, _) => { if (b.Enabled) b.BackColor = Clr.AccentHov; };
        b.MouseLeave += (_, _) => { if (b.Enabled) b.BackColor = Clr.Accent; };
        return b;
    }

    // ── Экран консоли ─────────────────────────────────────────────────────────

    void BuildConsolePanel()
    {
        _conPanel = new Panel { Dock = DockStyle.Fill, BackColor = Clr.LogBg, Visible = false };

        // Верхняя панель
        var top = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Clr.TopBar };
        top.Paint += (_, e) =>
        {
            using var pen = new Pen(Clr.CardEdge);
            e.Graphics.DrawLine(pen, 0, top.Height - 1, top.Width, top.Height - 1);
        };

        _btnDisc = new Button
        {
            Text = L.BtnDisconnect, Location = new Point(10, 9), Size = new Size(118, 28),
            BackColor = Color.FromArgb(130, 38, 52), ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
        };
        _btnDisc.FlatAppearance.BorderSize = 0;
        _btnDisc.Click += (_, _) => { _rcon.Disconnect(); ShowLogin(); };

        // Статус + адрес сервера
        var statusPanel = new Panel
        {
            Location = new Point(140, 0), Size = new Size(600, 46),
            BackColor = Color.Transparent,
        };

        _lblStatus = new Label
        {
            Location  = new Point(0, 0), Size = new Size(160, 46),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Clr.Green,
            Font      = new Font("Segoe UI", 10f, FontStyle.Bold),
        };

        _lblServer = new Label
        {
            Location  = new Point(162, 0), Size = new Size(360, 46),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Clr.FgDim,
            Font      = new Font("Segoe UI", 9f),
        };

        statusPanel.Controls.Add(_lblStatus);
        statusPanel.Controls.Add(_lblServer);
        top.Controls.Add(_btnDisc);
        top.Controls.Add(statusPanel);

        // Лог
        _log = new RichTextBox
        {
            Dock        = DockStyle.Fill,
            ReadOnly    = true,
            BackColor   = Clr.LogBg,
            ForeColor   = Clr.Fg,
            Font        = new Font("Consolas", 10f),
            ScrollBars  = RichTextBoxScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            WordWrap    = false,
            Padding     = new Padding(4),
        };

        // Нижняя панель
        var bot = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = Clr.TopBar };
        bot.Paint += (_, e) =>
        {
            using var pen = new Pen(Clr.CardEdge);
            e.Graphics.DrawLine(pen, 0, 0, bot.Width, 0);
        };

        _tbCmd = new TextBox
        {
            Location        = new Point(10, 11),
            Size            = new Size(bot.Width - 152, 26),
            Anchor          = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            BackColor       = Clr.Input,
            ForeColor       = Clr.Fg,
            BorderStyle     = BorderStyle.FixedSingle,
            Font            = new Font("Consolas", 10f),
            PlaceholderText = L.TypeCmd,
        };
        _tbCmd.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { DoSend(); e.SuppressKeyPress = true; } };

        _btnSend = new Button
        {
            Text      = L.BtnSend, Size = new Size(122, 26),
            Anchor    = AnchorStyles.Right | AnchorStyles.Top,
            BackColor = Color.FromArgb(38, 105, 65),
            ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
        };
        _btnSend.FlatAppearance.BorderSize = 0;
        _btnSend.Click += (_, _) => DoSend();

        bot.Controls.Add(_tbCmd);
        bot.Controls.Add(_btnSend);
        bot.Resize += (_, _) =>
        {
            _tbCmd.Width  = bot.Width - 152;
            _btnSend.Left = bot.Width - 140;
            _btnSend.Top  = 11;
        };

        _conPanel.Controls.Add(_log);
        _conPanel.Controls.Add(bot);
        _conPanel.Controls.Add(top);
        Controls.Add(_conPanel);
    }

    // ── Переключение экранов ──────────────────────────────────────────────────

    void ShowLogin()
    {
        _conPanel.Visible   = false;
        _loginPanel.Visible = true;
        _lblError.Text      = "";
        _btnConnect.Enabled = true;
        _btnConnect.BackColor = Clr.Accent;
        _btnConnect.Text    = L.BtnConnect;
        _log.Clear();
        _wasConnected = false;
        _cmbLang.BringToFront();
        _lblVersion.BringToFront();
    }

    void ShowConsole()
    {
        _loginPanel.Visible = false;
        _conPanel.Visible   = true;
        UpdateStatusLabel(true);
        _tbCmd.Focus();
        _cmbLang.BringToFront();
        _lblVersion.BringToFront();
    }

    // ── Действия ─────────────────────────────────────────────────────────────

    void DoConnect(object? sender, EventArgs? e)
    {
        if (!int.TryParse(_fPort.Value.Trim(), out int port))
        { _lblError.Text = L.ErrBadPort; return; }

        _lblError.Text        = "";
        _btnConnect.Enabled   = false;
        _btnConnect.BackColor = Clr.FgDim;
        _btnConnect.Text      = L.BtnConnecting;

        _cfg.Ip       = _fIp.Value.Trim();
        _cfg.Port     = port;
        _cfg.Remember = _chkRemember.Checked;
        _cfg.Password = _chkRemember.Checked ? _fPwd.Value : "";
        _cfg.Language = L.IsEn ? "en" : "ru";
        _cfg.Save(); // если Remember=false — удалит файл, если true — сохранит

        _connectedTo = $"{_cfg.Ip}:{_cfg.Port}";
        _rcon.Connect(_cfg.Ip, _cfg.Port, _fPwd.Value);
    }

    void DoSend()
    {
        var cmd = _tbCmd.Text.Trim();
        if (cmd.Length == 0) return;
        AppendLog("> " + cmd, Clr.Blue, showTime: true);
        _rcon.SendCommand(cmd);
        _tbCmd.Clear();
        _tbCmd.Focus();
    }

    // ── Опрос сообщений ───────────────────────────────────────────────────────

    void Poll(object? sender, EventArgs e)
    {
        string? msg;
        while ((msg = _rcon.Poll()) != null)
        {
            if (_loginPanel.Visible)
            {
                if (msg.StartsWith("[Info]"))
                { ShowConsole(); AppendLog(msg, Clr.Green, showTime: true); }
                else if (msg.StartsWith("[Error]"))
                {
                    _lblError.Text        = msg.Length > 8 ? msg[8..] : msg;
                    _btnConnect.Enabled   = true;
                    _btnConnect.BackColor = Clr.Accent;
                    _btnConnect.Text      = L.BtnConnect;
                }
            }
            else
            {
                var col = msg.StartsWith("[Error]")   ? Clr.Red
                        : msg.StartsWith("[Info]") || msg.StartsWith("[Warning]") ? Clr.Green
                        : Clr.Fg;
                AppendLog(msg, col, showTime: true);
            }
        }

        bool now = _rcon.IsConnected;
        if (_conPanel.Visible && _wasConnected && !now && !_rcon.IsConnecting)
        {
            UpdateStatusLabel(false);
            AppendLog(L.WarnLost, Clr.Yellow, showTime: true);
        }
        _wasConnected = now;
    }

    void AppendLog(string text, Color color, bool showTime = false)
    {
        if (_log.Lines.Length > 8000)
        { _log.Select(0, _log.GetFirstCharIndexFromLine(500)); _log.SelectedText = ""; }

        _log.SelectionStart  = _log.TextLength;
        _log.SelectionLength = 0;

        if (showTime)
        {
            _log.SelectionColor = Clr.TimeColor;
            _log.AppendText($"[{DateTime.Now:HH:mm:ss}] ");
        }

        _log.SelectionColor = color;
        _log.AppendText(text + "\n");
        _log.ScrollToCaret();
    }
}

// ── Точка входа ───────────────────────────────────────────────────────────────
static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}

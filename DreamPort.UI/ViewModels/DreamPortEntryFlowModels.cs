using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DreamPort.UI.ViewModels;

/// <summary>状态提示条配色级别（映射 MyHint.Themes）。</summary>
public enum DreamPortHintLevel
{
    Info = 0,
    Warning = 1,
    Error = 2
}

/// <summary>六步入服流程的单步展示项。</summary>
public sealed record DreamPortProgressStepVm(
    string Key,
    string Label,
    bool Done,
    bool IsCurrent,
    bool Available,
    string UnavailableReason,
    string? Via)
{
    public string StatusText
    {
        get
        {
            if (Done)
                return string.Equals(Via, "invite", StringComparison.OrdinalIgnoreCase) ? "✓ 已完成（邀请）" : "✓ 已完成";
            if (!Available)
                return "暂不可用";
            return IsCurrent ? "▶ 当前步骤" : "待完成";
        }
    }

    public string DetailText => !Available && !string.IsNullOrWhiteSpace(UnavailableReason)
        ? $"{Label} · {StatusText}（{UnavailableReason}）"
        : $"{Label} · {StatusText}";
}

/// <summary>已登录设备会话列表项。</summary>
public sealed class DreamPortSessionItemVm
{
    public DreamPortSessionItemVm(
        string sessionId,
        string deviceName,
        string platform,
        string clientVersion,
        string ip,
        long lastSeenAtMs,
        bool revoked,
        bool isCurrent,
        ICommand? revokeCommand)
    {
        SessionId = sessionId;
        DeviceName = string.IsNullOrWhiteSpace(deviceName) ? "未知设备" : deviceName;
        Platform = string.IsNullOrWhiteSpace(platform) ? "unknown" : platform;
        ClientVersion = string.IsNullOrWhiteSpace(clientVersion) ? "-" : clientVersion;
        Ip = string.IsNullOrWhiteSpace(ip) ? "-" : ip;
        LastSeenAtMs = lastSeenAtMs;
        Revoked = revoked;
        IsCurrent = isCurrent;
        RevokeCommand = revokeCommand;
    }

    public string SessionId { get; }
    public string DeviceName { get; }
    public string Platform { get; }
    public string ClientVersion { get; }
    public string Ip { get; }
    public long LastSeenAtMs { get; }
    public bool Revoked { get; }
    public bool IsCurrent { get; }
    public ICommand? RevokeCommand { get; }

    public bool CanRevoke => !IsCurrent && !Revoked;

    public string HeaderText => IsCurrent ? $"{DeviceName} [当前设备]" : DeviceName;

    public string MetaText => $"平台: {Platform} | 版本: {ClientVersion} | IP: {Ip} | 最近活跃: {FormatTimestamp(LastSeenAtMs)}";

    private static string FormatTimestamp(long ms)
    {
        if (ms <= 0)
            return "-";
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }
        catch
        {
            return "-";
        }
    }
}

/// <summary>问卷选项视图模型（同时支持代码层 IsSelected 与 PCL MyCheckBox.Checked 双向绑定）。</summary>
public sealed partial class DreamPortQuestionOptionVm : ObservableObject
{
    private readonly Action<DreamPortQuestionOptionVm>? _onSelectedChanged;
    private bool _isSelected;

    public DreamPortQuestionOptionVm(long id, string text, Action<DreamPortQuestionOptionVm>? onSelectedChanged = null)
    {
        Id = id;
        Text = text;
        _onSelectedChanged = onSelectedChanged;
    }

    public long Id { get; }

    public string Text { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(IsChecked));
                _onSelectedChanged?.Invoke(this);
            }
        }
    }

    public bool? IsChecked
    {
        get => _isSelected;
        set => IsSelected = value == true;
    }
}

/// <summary>入服问卷题目视图模型。</summary>
public sealed partial class DreamPortQuestionVm : ObservableObject
{
    [ObservableProperty] private string _answerText = string.Empty;

    public DreamPortQuestionVm(
        int order,
        long id,
        string text,
        string type,
        bool required,
        int maxScore,
        bool multiline,
        int minLength,
        int maxLength,
        int minSelections,
        int maxSelections,
        string placeholder,
        IEnumerable<DreamPort.Core.Api.DreamPortQuestionOption> options)
    {
        Order = order;
        Id = id;
        Text = text;
        Type = type;
        Required = required;
        MaxScore = maxScore;
        Multiline = multiline;
        MinLength = minLength;
        MaxLength = maxLength;
        MinSelections = minSelections;
        MaxSelections = maxSelections;
        Placeholder = placeholder;

        Options = new ObservableCollection<DreamPortQuestionOptionVm>(
            options.Select(o => new DreamPortQuestionOptionVm(o.Id, o.Text, OnOptionSelectedChanged)));
    }

    public int Order { get; }
    public long Id { get; }
    public string Text { get; }
    public string Type { get; }
    public bool Required { get; }
    public int MaxScore { get; }
    public bool Multiline { get; }
    public int MinLength { get; }
    public int MaxLength { get; }
    public int MinSelections { get; }
    public int MaxSelections { get; }
    public string Placeholder { get; }

    public ObservableCollection<DreamPortQuestionOptionVm> Options { get; }

    public bool IsTextQuestion =>
        Type is "text" or "essay" or "fill_blank" || Options.Count == 0;

    public bool IsChoiceQuestion => !IsTextQuestion;

    public string HeaderText
    {
        get
        {
            var typeLabel = Type switch
            {
                "single" => "单选",
                "multiple" => "多选",
                "fill_blank" => "填空",
                _ => "问答"
            };
            var req = Required ? "，必答" : "";
            return $"Q{Order}. [{typeLabel}] {Text}（满分 {MaxScore} 分{req}）";
        }
    }

    public string ConstraintText
    {
        get
        {
            if (IsTextQuestion)
            {
                if (MinLength > 0 && MaxLength > 0)
                    return $"作答长度要求：{MinLength}~{MaxLength} 字";
                if (MinLength > 0)
                    return $"至少输入 {MinLength} 字";
                if (MaxLength > 0)
                    return $"最多 {MaxLength} 字";
                return string.IsNullOrWhiteSpace(Placeholder) ? "请输入回答" : Placeholder;
            }

            if (Type == "multiple")
            {
                if (MinSelections > 0 && MaxSelections > 0)
                    return $"请选择 {MinSelections}~{MaxSelections} 项";
                if (MinSelections > 0)
                    return $"至少选择 {MinSelections} 项";
                return "可选择多项";
            }

            return "请选择 1 项";
        }
    }

    private void OnOptionSelectedChanged(DreamPortQuestionOptionVm changed)
    {
        if (Type != "single" || !changed.IsSelected)
            return;

        foreach (var opt in Options)
        {
            if (!ReferenceEquals(opt, changed) && opt.IsSelected)
                opt.IsSelected = false;
        }
    }
}

/// <summary>问卷 SSE 逐题评分实时反馈项。</summary>
public sealed record DreamPortScoreFeedbackVm(
    string HeaderText,
    string ScoreText,
    string ReasonText);

/// <summary>玩家称号选择项。</summary>
public sealed record DreamPortTitleOption(
    string Code,
    string Name,
    string Desc,
    string Color,
    bool IsEquipped)
{
    public string DisplayText => string.IsNullOrWhiteSpace(Desc)
        ? (IsEquipped ? $"{Name}（当前佩戴）" : Name)
        : (IsEquipped ? $"{Name}（当前佩戴）- {Desc}" : $"{Name} - {Desc}");

    public override string ToString() => DisplayText;
}

/// <summary>站内通知列表项。</summary>
public sealed partial class DreamPortNotificationItemVm : ObservableObject
{
    [ObservableProperty] private bool _read;

    public DreamPortNotificationItemVm(
        long id,
        string type,
        string title,
        string body,
        string? link,
        bool read,
        long createdAtMs,
        bool hasSafeLink,
        ICommand? markReadCommand,
        ICommand? openLinkCommand)
    {
        Id = id;
        Type = type;
        Title = title;
        Body = body;
        Link = link;
        _read = read;
        CreatedAtMs = createdAtMs;
        HasSafeLink = hasSafeLink;
        MarkReadCommand = markReadCommand;
        OpenLinkCommand = openLinkCommand;
    }

    public long Id { get; }
    public string Type { get; }
    public string Title { get; }
    public string Body { get; }
    public string? Link { get; }
    public long CreatedAtMs { get; }
    public bool HasSafeLink { get; }
    public ICommand? MarkReadCommand { get; }
    public ICommand? OpenLinkCommand { get; }

    public bool IsUnread => !Read;
    public bool HasBody => !string.IsNullOrWhiteSpace(Body);
    public string HeaderText => Read ? Title : $"[未读] {Title}";
    public string MetaText => $"{FormatTimestamp(CreatedAtMs)} · {(Read ? "已读" : "未读")}";

    partial void OnReadChanged(bool value)
    {
        OnPropertyChanged(nameof(IsUnread));
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(MetaText));
    }

    private static string FormatTimestamp(long ms)
    {
        if (ms <= 0)
            return "刚刚";
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }
        catch
        {
            return "-";
        }
    }
}

/// <summary>服务器公告列表项。</summary>
public sealed record DreamPortAnnouncementItemVm(
    string Title,
    string Body,
    string DateText,
    string? Tag,
    bool Important)
{
    public string HeaderText => Important ? $"[重要] {Title}" : Title;

    public bool HasBody => !string.IsNullOrWhiteSpace(Body);

    public string MetaText
    {
        get
        {
            var parts = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(Tag))
                parts.Add(Tag);
            if (!string.IsNullOrWhiteSpace(DateText))
                parts.Add(DateText);
            return parts.Count > 0 ? string.Join(" · ", parts) : "站点公告";
        }
    }
}

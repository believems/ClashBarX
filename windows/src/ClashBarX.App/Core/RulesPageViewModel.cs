using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ClashBarX.Core.Models;
using ClashBarX.Core.Rules;

namespace ClashBarX.App.Core;

/// <summary>规则类型过滤（源：RulesViewModel.RulesTypeFilter——小写子串归类）。</summary>
public enum RulesTypeFilter
{
    All,
    Domain,
    Ip,
    RuleSet,
    Other,
}

/// <summary>规则页模型（源：RulesViewModel + RulesTabView）。</summary>
public sealed class RulesPageViewModel : INotifyPropertyChanged
{
    /// <summary>渲染保留上限（源：RulesSummary.retainedRuleLimit，模型层已截断）。</summary>
    private const int RenderLimit = 500;

    private string _filterText = string.Empty;
    private RulesTypeFilter _typeFilter = RulesTypeFilter.All;
    private string? _policyFilter;
    private bool _groupByPolicy;
    private bool _isRefreshing;
    private string _statsText = ClashBarX.Core.L10n.CoreL10n.T("rules.stats.summary", 0, 0);
    private string _countText = "0/0";

    private IReadOnlyList<RuleItem> _rules = Array.Empty<RuleItem>();
    private Dictionary<string, ProviderDetail> _providerLookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _collapsedPolicies = new(StringComparer.Ordinal);

    public RulesPageViewModel(Func<Task> refresh, Func<bool> canRefresh)
    {
        RefreshCommand = new RelayCommand(_ =>
        {
            if (!canRefresh() || _isRefreshing)
            {
                return Task.CompletedTask;
            }

            return RefreshCoreAsync(refresh);
        });
        ToggleGroupCommand = new RelayCommand(parameter =>
        {
            if (parameter is string policy)
            {
                ToggleGroup(policy);
            }

            return Task.CompletedTask;
        });
    }

    public ObservableCollection<object> Rows { get; } = new();

    /// <summary>类型过滤下拉选项（源：ui.rules.filter.type.*）。</summary>
    public IReadOnlyList<EnumOption<RulesTypeFilter>> TypeFilterOptions { get; } = new[]
    {
        new EnumOption<RulesTypeFilter>(ClashBarX.Core.L10n.CoreL10n.T("rules.filter.all"), RulesTypeFilter.All),
        new EnumOption<RulesTypeFilter>(ClashBarX.Core.L10n.CoreL10n.T("rules.filter.domain"), RulesTypeFilter.Domain),
        new EnumOption<RulesTypeFilter>(ClashBarX.Core.L10n.CoreL10n.T("rules.filter.ip"), RulesTypeFilter.Ip),
        new EnumOption<RulesTypeFilter>(ClashBarX.Core.L10n.CoreL10n.T("rules.filter.ruleset"), RulesTypeFilter.RuleSet),
        new EnumOption<RulesTypeFilter>(ClashBarX.Core.L10n.CoreL10n.T("rules.filter.other"), RulesTypeFilter.Other),
    };

    /// <summary>策略下拉选项（全部 + 去重排序的 proxy 名；源：policyOptions）。
    /// 构造时预置「全部策略」占位，避免空态下拉空白（规则集未加载时）。</summary>
    public ObservableCollection<PolicyOption> PolicyOptions { get; } = new()
    {
        new PolicyOption(ClashBarX.Core.L10n.CoreL10n.T("rules.policy.all"), null),
    };

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
            {
                RebuildRows();
            }
        }
    }

    public RulesTypeFilter TypeFilter
    {
        get => _typeFilter;
        set
        {
            if (SetProperty(ref _typeFilter, value))
            {
                RebuildRows();
            }
        }
    }

    public string? PolicyFilter
    {
        get => _policyFilter;
        private set
        {
            if (SetProperty(ref _policyFilter, value))
            {
                RebuildRows();
            }
        }
    }

    /// <summary>
    /// 策略下拉选中项（对象级绑定——SelectedValue 反查在重建的 ItemsSource 上有
    /// 时序空选中坑，与 TUN stack 下拉同根因）。
    /// </summary>
    public PolicyOption? PolicySelection
    {
        get => _policySelection;
        set
        {
            if (value is null || ReferenceEquals(_policySelection, value))
            {
                return;
            }

            _policySelection = value;
            Raise(nameof(PolicySelection));
            PolicyFilter = value.Value;
        }
    }

    private PolicyOption? _policySelection;

    /// <summary>按策略分组（默认关；源：clashbar.rules.group_by_policy.v1）。</summary>
    public bool GroupByPolicy
    {
        get => _groupByPolicy;
        set
        {
            if (SetProperty(ref _groupByPolicy, value))
            {
                RebuildRows();
            }
        }
    }

    /// <summary>顶部统计条（源：规则 chip + 规则集 chip）。</summary>
    public string StatsText
    {
        get => _statsText;
        private set => SetProperty(ref _statsText, value);
    }

    /// <summary>当前/总数徽章（源：fractionSummaryBadge）。</summary>
    public string CountText
    {
        get => _countText;
        private set => SetProperty(ref _countText, value);
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set => SetProperty(ref _isRefreshing, value);
    }

    public ICommand RefreshCommand { get; }

    /// <summary>组头点击展开/折叠（CommandParameter = 策略名）。</summary>
    public ICommand ToggleGroupCommand { get; }

    /// <summary>喂入面板数据并重算（源：onChange ruleItems 重算，不重拉数据）。</summary>
    public void ApplyBoard(RulesBoard board)
    {
        _rules = board.Rules;
        // providerLookup：key 与 name 双写、全部小写（源：makeProviderLookup）
        _providerLookup = new Dictionary<string, ProviderDetail>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, ProviderDetail> pair in board.RuleProviders)
        {
            _providerLookup.TryAdd(pair.Key, pair.Value);
            if (pair.Value.Name is { Length: > 0 } name)
            {
                _providerLookup.TryAdd(name, pair.Value);
            }
        }

        RebuildPolicyOptions();
        StatsText = ClashBarX.Core.L10n.CoreL10n.T("rules.stats.summary", board.RulesCount, board.ProviderRuleCount);
        RebuildRows();
    }

    /// <summary>组头点击展开/折叠（源：expandedPolicies 局部状态，不持久化）。</summary>
    internal void ToggleGroup(string policy)
    {
        if (!_collapsedPolicies.Remove(policy))
        {
            _collapsedPolicies.Add(policy);
        }

        RebuildRows();
    }

    private async Task RefreshCoreAsync(Func<Task> refresh)
    {
        IsRefreshing = true;
        try
        {
            await refresh();
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private void RebuildPolicyOptions()
    {
        string? current = PolicyFilter;
        PolicyOptions.Clear();
        PolicyOptions.Add(new PolicyOption(ClashBarX.Core.L10n.CoreL10n.T("rules.policy.all"), null));
        foreach (string policy in _rules
                     .Select(rule => rule.Proxy)
                     .OfType<string>()
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(policy => policy, StringComparer.OrdinalIgnoreCase))
        {
            PolicyOptions.Add(new PolicyOption(policy, policy));
        }

        // 选中项按值恢复（对象更替不丢选中；选中策略从新数据消失时回落「全部」）
        PolicyOption? selection = PolicyOptions.FirstOrDefault(option => option.Value == current)
            ?? PolicyOptions.FirstOrDefault(option => option.Value is null);
        _policySelection = selection;
        Raise(nameof(PolicySelection));
        if (selection?.Value != current)
        {
            PolicyFilter = null;   // 经 setter 触发行重建（回落全量）
        }
    }

    private void RebuildRows()
    {
        string keyword = _filterText.Trim();

        IEnumerable<RuleItem> source = _rules;

        // 先按策略精确匹配 + 关键字（源：updateVisibleRules 的 base 语义）
        if (_policyFilter is { Length: > 0 })
        {
            source = source.Where(rule => rule.Proxy == _policyFilter);
        }

        if (keyword.Length > 0)
        {
            source = source.Where(rule =>
                SearchText(rule).Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        List<RuleItem> typed = _typeFilter == RulesTypeFilter.All
            ? source.ToList()
            : source.Where(rule => MatchesType(rule.Type, _typeFilter)).ToList();

        CountText = $"{typed.Count}/{_rules.Count}";
        var capped = typed.Take(RenderLimit).ToList();

        Rows.Clear();
        if (_groupByPolicy)
        {
            // 按 proxy 分桶：组按规则数降序、同数按名称序（源：makeGroups）
            foreach (var group in capped
                         .GroupBy(rule => rule.Proxy ?? string.Empty)
                         .OrderByDescending(group => group.Count())
                         .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                bool collapsed = _collapsedPolicies.Contains(group.Key);
                Rows.Add(new RuleGroupHeaderViewModel(group.Key, group.Count(), collapsed));
                if (!collapsed)
                {
                    foreach (RuleItem rule in group)
                    {
                        Rows.Add(CreateRow(rule, hidePolicy: true));
                    }
                }
            }
        }
        else
        {
            foreach (RuleItem rule in capped)
            {
                Rows.Add(CreateRow(rule, hidePolicy: false));
            }
        }
    }

    /// <summary>类型归类（源：小写子串判断——rule-set/ruleset→规则集、domain/geosite→域名、ip/geoip→IP）。</summary>
    internal static bool MatchesType(string? type, RulesTypeFilter filter)
    {
        string lowered = (type ?? string.Empty).ToLowerInvariant();
        return filter switch
        {
            RulesTypeFilter.RuleSet => lowered.Contains("rule-set") || lowered.Contains("ruleset"),
            RulesTypeFilter.Domain => lowered.Contains("domain") || lowered.Contains("geosite"),
            RulesTypeFilter.Ip => lowered.Contains("ip") || lowered.Contains("geoip"),
            RulesTypeFilter.Other => !IsDomain(lowered) && !IsIp(lowered) && !IsRuleSet(lowered),
            _ => true,
        };
    }

    private static bool IsDomain(string lowered) =>
        lowered.Contains("domain") || lowered.Contains("geosite");

    private static bool IsIp(string lowered) =>
        lowered.Contains("ip") || lowered.Contains("geoip");

    private static bool IsRuleSet(string lowered) =>
        lowered.Contains("rule-set") || lowered.Contains("ruleset");

    /// <summary>搜索匹配串（源：payload + type + proxy 空格拼接）。</summary>
    internal static string SearchText(RuleItem rule) =>
        string.Join(' ', rule.Payload ?? string.Empty, rule.Type ?? string.Empty, rule.Proxy ?? string.Empty);

    private RuleRowViewModel CreateRow(RuleItem rule, bool hidePolicy)
    {
        // 统计列：payload 匹配 provider 时显示规则条数 + 更新时间（源：ruleStats，非逐条命中数）
        string statsText = "0";
        if (rule.Payload is { Length: > 0 } payload
            && _providerLookup.TryGetValue(payload, out ProviderDetail? provider)
            && provider.RuleCount is { } count)
        {
            statsText = count.ToString();
        }

        return new RuleRowViewModel(
            string.IsNullOrWhiteSpace(rule.Payload) ? "—" : rule.Payload!,
            rule.Type ?? string.Empty,
            hidePolicy ? null : rule.Proxy,
            statsText);
    }

    private void Raise(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

/// <summary>规则行：目标（payload 主行）+ 类型副行 + 策略列 + 统计列（源：rulesRow）。</summary>
public sealed record RuleRowViewModel(
    string Target,
    string TypeText,
    string? Policy,
    string StatsText)
{
    public string PolicyText => Policy ?? string.Empty;
}

/// <summary>分组头：策略名 + 规则数徽章 + 展开态（源：组头行）。</summary>
public sealed record RuleGroupHeaderViewModel(string Policy, int Count, bool IsCollapsed);

/// <summary>策略下拉选项（策略名为 string，不适用枚举选项）。</summary>
public sealed record PolicyOption(string Label, string? Value);

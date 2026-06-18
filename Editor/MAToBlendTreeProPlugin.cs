using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using nadena.dev.ndmf.fluent;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

[assembly: ExportsPlugin(typeof(zhuozhi.MA2BTPro.MAToBlendTreeProPlugin))]

namespace zhuozhi.MA2BTPro
{

[RunsOnPlatforms(WellKnownPlatforms.VRChatAvatar30)]
internal class MAToBlendTreeProPlugin : Plugin<MAToBlendTreeProPlugin>
{
    public override string QualifiedName => "com.zhuozhi.ma2btpro";
    public override string DisplayName => "MA2BT Pro";
    public override Color? ThemeColor => new Color(0.55f, 0.2f, 0.85f, 1);

    protected override void Configure()
    {
        Sequence seq = InPhase(BuildPhase.Transforming);
        seq.AfterPlugin("nadena.dev.modular-avatar");
        seq.WithRequiredExtension(typeof(AnimatorServicesContext), s =>
        {
            s.Run("MA2BT Pro", ctx =>
            {
                var settings = ctx.AvatarRootObject.GetComponent<MAToBlendTreePro>();
                if (settings == null) return;

                if (!MAToBlendTreePro.IsNDMFCompatible(out var compatibilityMessage))
                {
                    Debug.LogError("[MA2BT Pro] 已跳过：" + compatibilityMessage);
                    return;
                }

                var optimizer = new LayerToBlendTreeOptimizer(ctx, settings);
                optimizer.Process();

                Object.DestroyImmediate(settings, true);
            });
        });
    }
}

#region 数据

internal class AnalyzedLayer
{
    public VirtualLayer Layer;
    public bool IsConvertible;
    public string RejectReason;
    public string ParameterName;
    public bool IsInverted;
    public List<StateInfo> States = new();
    public int OriginalIndex;
    public bool IsExternalLayer;
    public bool HasMultipleConditionalStates;
    public bool RequiresNestedLayerTree;
    public List<string> MainParameterNames = new();
}

internal class StateInfo
{
    public bool IsDefault;
    public int Order;
    public string StateName;
    public string ParameterName;
    public bool IsInverted;
    public float ThresholdLo = float.NaN;
    public float ThresholdHi = float.NaN;
    public VirtualMotion Motion;
    public List<SecondaryCondition> SecondaryConditions = new();
}

internal class SecondaryCondition
{
    public string ParameterName;
    public bool ActiveWhenGreater;
    public float Threshold = 0.5f;
}

internal class ConditionRange
{
    public string ParameterName;
    public float ThresholdLo = float.NaN;
    public float ThresholdHi = float.NaN;
    public bool IsInverted;
    public bool AddSafetyGuardForOpenRange;
}

internal class LayerThresholdMotion
{
    public VirtualMotion Motion;
    public bool CanMergeIntoClip;
}

internal class ParameterGroup
{
    public string ParameterName;
    public List<AnalyzedLayer> Layers = new();
    public List<float> Thresholds = new();
}

internal class NestedLayerGroup
{
    public string Signature;
    public List<AnalyzedLayer> Layers = new();

    public int OriginalIndex => Layers.Count == 0
        ? int.MaxValue
        : Layers.Min(l => l.OriginalIndex);

    public string DisplayName => Layers.Count == 0
        ? "EmptyNestedGroup"
        : Layers.Count == 1
            ? Layers[0].Layer.Name
            : $"{Layers[0].Layer.Name} 等 {Layers.Count} 个图层";
}

#endregion


internal class LayerToBlendTreeOptimizer
{
    const string ROOT_PARAM = "zhz/1";
    const string BLEND_TREE_LAYER_NAME = "MA_To_BlendTree_Layer";

    readonly MAToBlendTreePro _settings;
    readonly VirtualAnimatorController _fx;

    VirtualClip _sharedEmptyClip;
    int _sameParameterNestedCheckPruned;
    readonly Dictionary<string, int> _autoParamMaxValues = new Dictionary<string, int>();
    readonly Dictionary<string, string> _prefixProtectedParameterReasons = new Dictionary<string, string>();

    public LayerToBlendTreeOptimizer(BuildContext ctx, MAToBlendTreePro settings)
    {
        _settings = settings;
        var asc = ctx.Extension<AnimatorServicesContext>();
        _fx = asc.ControllerContext.Controllers[VRCAvatarDescriptor.AnimLayerType.FX];
    }

    public void Process()
    {
        if (!MAToBlendTreePro.IsNDMFCompatible(out var compatibilityMessage))
        {
            Debug.LogError("[MA2BT Pro] 已跳过：" + compatibilityMessage);
            return;
        }

        var analyzedLayers = AnalyzeAllLayers();

        _prefixProtectedParameterReasons.Clear();
        foreach (var kv in CollectPrefixProtectedParameters())
            _prefixProtectedParameterReasons[kv.Key] = kv.Value;

        ApplyPrefixProtectedParameterExclusions(analyzedLayers);
        ApplySharedBoolIntParameterProtection(analyzedLayers);

        var convertibleLayers = analyzedLayers.Where(l => l.IsConvertible).ToList();
        if (convertibleLayers.Count == 0)
        {
            Debug.Log("[MA2BT Pro] 未找到可转换的 MA Responsive 图层，跳过优化。");
            return;
        }

        int rejectedCount = 0;
        foreach (var layer in analyzedLayers)
        {
            if (!layer.IsConvertible && layer.RejectReason != null)
            {
                Debug.Log($"[MA2BT Pro] 保留图层 \"{layer.Layer.Name}\"：{FormatRejectReason(layer.RejectReason)}");
                rejectedCount++;
            }
        }

        int externalCount = convertibleLayers.Count(l => l.IsExternalLayer);
        string externalNote = externalCount > 0 ? $"（包含 {externalCount} 个非 MA 图层）" : "";
        Debug.Log($"[MA2BT Pro] 找到 {convertibleLayers.Count} 个可转换图层{externalNote}，保留 {rejectedCount} 个不可转换图层。");
        Debug.Log($"[MA2BT Pro] 设置：Compact Mode={_settings.compactMode}，Multi-State Layers={_settings.convertMultiState}，Scan All Layers={_settings.scanAllLayers}，合并相同嵌套树/动画={_settings.mergeIdenticalBlendTreesAndAnimations}。");
        Debug.Log($"[MA2BT Pro] 排除项：图层名前缀=[{FormatPrefixList(_settings.excludedLayerPrefixes)}]，状态名前缀=[{FormatPrefixList(_settings.excludedStatePrefixes)}]，参数名前缀=[{FormatPrefixList(GetExcludedParameterPrefixes())}]。");
        if (_prefixProtectedParameterReasons.Count > 0)
        {
            Debug.Log("[MA2BT Pro] 前缀保护参数：" + string.Join(", ",
                _prefixProtectedParameterReasons
                    .OrderBy(kv => kv.Key)
                    .Select(kv => $"{kv.Key}（{kv.Value}）")));
        }
        foreach (var layer in convertibleLayers.OrderBy(l => l.OriginalIndex))
        {
            Debug.Log($"[MA2BT Pro] 可转换图层 #{layer.OriginalIndex} \"{layer.Layer.Name}\"：{DescribeLayerStrategy(layer)}");
        }

        var layersToRemove = new HashSet<VirtualLayer>(convertibleLayers.Select(l => l.Layer));
        var migratedParameterTypes = GetMigratedBoolIntParameterTypes(convertibleLayers);
        RewriteFloatMigratedParameterConditions(migratedParameterTypes, layersToRemove);

        CacheAutoParamMaxValues(convertibleLayers);
        if (_autoParamMaxValues.Count > 0)
        {
            Debug.Log($"[MA2BT Pro] 自动参数最大值限制：{string.Join(", ", _autoParamMaxValues.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}<= {kv.Value}"))}");
        }

        // 单主参数图层仍然按参数分组；一个图层内出现多个主参数时，改走图层级嵌套树。
        var nestedOnlyLayers = convertibleLayers
            .Where(l => l.RequiresNestedLayerTree)
            .OrderBy(l => l.OriginalIndex)
            .ToList();
        var groupedLayers = convertibleLayers
            .Where(l => !l.RequiresNestedLayerTree)
            .OrderBy(l => l.OriginalIndex)
            .ToList();

        // 按参数分组
        var paramGroups = GroupByParameter(groupedLayers);

        foreach (var layer in nestedOnlyLayers)
            EnsureParametersForLayer(layer);

        var nestedGroups = GroupNestedLayers(nestedOnlyLayers);

        // 构建混合树
        var rootBlendTree = BuildRootBlendTree(paramGroups, nestedGroups);

        // 注入到 FX
        EnsureFloatParameter(ROOT_PARAM, 1f);
        InjectBlendTreeLayer(rootBlendTree);

        // 移除转换后的层
        _fx.RemoveLayers(l => layersToRemove.Contains(l));

        Debug.Log($"[MA2BT Pro] 完成：已将 {convertibleLayers.Count} 个图层合并为 {paramGroups.Count} 个参数混合树节点、{nestedGroups.Count} 个嵌套混合树节点。");
        string thresholdModeName = _settings.compactMode ? "紧凑稀疏模式" : "原始完整模式（嵌套树从首个保护阈值开始）";
        foreach (var group in paramGroups)
        {
            Debug.Log($"[MA2BT Pro]   参数 \"{group.ParameterName}\"：{group.Layers.Count} 个图层 > {group.Thresholds.Count} 个阈值 " +
              $"（{thresholdModeName}）[{string.Join(", ", group.Thresholds)}]");
        }
        if (_sameParameterNestedCheckPruned > 0)
        {
            Debug.Log($"[MA2BT Pro]   已优化 {_sameParameterNestedCheckPruned} 个父级已限制参数范围的重复嵌套判断。");
        }

        foreach (var group in nestedGroups)
        {
            if (group.Layers.Count > 1)
            {
                string layerNames = string.Join(", ", group.Layers.Select(l => $"\"{l.Layer.Name}\""));
                string paramNames = string.Join(", ", group.Layers[0].MainParameterNames);
                Debug.Log($"[MA2BT Pro]   嵌套组合并 \"{group.DisplayName}\"：{group.Layers.Count} 个图层合并为 1 个嵌套混合树，图层=[{layerNames}]，参数=[{paramNames}]");
            }
            else
            {
                var layer = group.Layers[0];
                Debug.Log($"[MA2BT Pro]   嵌套图层 \"{layer.Layer.Name}\"：{layer.States.Count(s => !s.IsDefault)} 个状态，参数=[{string.Join(", ", layer.MainParameterNames)}]");
            }
        }
    }

    #region 扫描

    bool IsMAResponsiveLayer(string layerName)
    {
        if (string.IsNullOrEmpty(layerName) || _settings.maResponsivePrefixes == null)
            return false;

        foreach (var prefix in _settings.maResponsivePrefixes)
        {
            if (!string.IsNullOrEmpty(prefix) && layerName.StartsWith(prefix))
                return true;
        }

        return false;
    }

    List<AnalyzedLayer> AnalyzeAllLayers()
    {
        var results = new List<AnalyzedLayer>();
        int index = 0;

        foreach (var layer in _fx.Layers)
        {
            bool isMALayer = IsMAResponsiveLayer(layer.Name);
            bool shouldAnalyze = isMALayer || _settings.scanAllLayers;

            if (shouldAnalyze && layer.Name != BLEND_TREE_LAYER_NAME)
            {
                if (HasExcludedPrefix(layer.Name, _settings.excludedLayerPrefixes, out var matchedLayerPrefix))
                {
                    results.Add(new AnalyzedLayer
                    {
                        Layer = layer,
                        OriginalIndex = index,
                        IsConvertible = false,
                        IsExternalLayer = !isMALayer,
                        RejectReason = $"Excluded by layer prefix: {matchedLayerPrefix}"
                    });
                    index++;
                    continue;
                }

                var analyzed = AnalyzeLayer(layer, index, isMALayer);
                if (!isMALayer)
                {
                    analyzed.IsExternalLayer = true;

                    // Scan All Layers 下的非 MA 图层：原 Entry Transition 规则失败后，
                    // 再额外尝试严格识别手动制作的简单二状态图层。
                    if (!analyzed.IsConvertible)
                    {
                        var originalRejectReason = analyzed.RejectReason;
                        var manualAnalyzed = AnalyzeManualSimpleLayer(layer, index, originalRejectReason);
                        manualAnalyzed.IsExternalLayer = true;

                        if (manualAnalyzed.IsConvertible)
                        {
                            analyzed = manualAnalyzed;
                        }
                        else if (!string.IsNullOrEmpty(manualAnalyzed.RejectReason))
                        {
                            analyzed.RejectReason = string.IsNullOrEmpty(originalRejectReason)
                                ? $"Manual simple layer rejected: {manualAnalyzed.RejectReason}"
                                : $"{originalRejectReason}; Manual simple layer rejected: {manualAnalyzed.RejectReason}";
                        }
                    }
                }

                results.Add(analyzed);
            }
            index++;
        }

        return results;
    }

    AnalyzedLayer AnalyzeLayer(VirtualLayer layer, int index, bool isMALayer)
    {
        var result = new AnalyzedLayer
        {
            Layer = layer,
            OriginalIndex = index,
            IsConvertible = false
        };

        var sm = layer.StateMachine;
        if (sm == null)
        {
            result.RejectReason = "No state machine";
            return result;
        }

        if (StateMachineHasBlockingBehaviours(sm, isMALayer, out var stateMachineBehaviourSummary))
        {
            result.RejectReason = $"StateMachine 存在行为组件，跳过整个图层：{stateMachineBehaviourSummary}";
            return result;
        }

        var states = sm.States;
        if (states.Count < 2)
        {
            result.RejectReason = $"Insufficient state count ({states.Count})";
            return result;
        }

        var defaultState = sm.DefaultState;
        if (defaultState == null)
        {
            result.RejectReason = "No default state";
            return result;
        }

        foreach (var childState in states)
        {
            var stateName = childState.State?.Name;
            if (HasExcludedPrefix(stateName, _settings.excludedStatePrefixes, out var matchedStatePrefix))
            {
                result.RejectReason = $"Excluded by state prefix: {matchedStatePrefix} (state \"{stateName}\")";
                return result;
            }
        }

        // MA 会给部分响应式图层补上 MMD 控制行为，这个可以放心跳过。
        // 其它行为还是保留原图层，避免把用户逻辑一起揉进 BlendTree。
        foreach (var childState in states)
        {
            if (StateHasBlockingBehaviours(childState.State, isMALayer, out var behaviourSummary))
            {
                string stateName = childState.State?.Name ?? "<Unnamed>";
                result.RejectReason = $"状态 \"{stateName}\" 存在行为组件，跳过整个图层：{behaviourSummary}";
                return result;
            }
        }

        var conditionalStates = states.Where(cs => cs.State != defaultState).ToList();
        result.HasMultipleConditionalStates = conditionalStates.Count > 1;

        if (!_settings.convertMultiState && result.HasMultipleConditionalStates)
        {
            result.RejectReason = $"Multi-state layer ({conditionalStates.Count} conditional states), enable Multi-State Layers";
            return result;
        }

        var entryTransitions = sm.EntryTransitions;
        if (entryTransitions.Count == 0)
        {
            result.RejectReason = "No Entry Transition";
            return result;
        }

        var stateInfos = new List<StateInfo>();

        stateInfos.Add(new StateInfo
        {
            IsDefault = true,
            Order = -1,
            StateName = defaultState.Name,
            Motion = defaultState.Motion
        });

        var orderedConditionalStates = OrderConditionalStatesByEntryTransitions(conditionalStates, entryTransitions, defaultState);

        var entryTransitionsByState = orderedConditionalStates
            .Select(cs => new
            {
                ChildState = cs,
                EntryTransitions = entryTransitions.Where(t => t.DestinationState == cs.State).ToList()
            })
            .ToList();

        var commonConditionParameters = GetCommonConditionParameters(entryTransitionsByState
            .Select(x => x.EntryTransitions));

        for (int stateOrder = 0; stateOrder < entryTransitionsByState.Count; stateOrder++)
        {
            var cs = entryTransitionsByState[stateOrder].ChildState;
            var state = cs.State;

            var entryTrans = entryTransitionsByState[stateOrder].EntryTransitions;
            if (entryTrans.Count == 0)
            {
                result.RejectReason = $"State \"{state.Name}\" has no corresponding Entry Transition";
                return result;
            }

            // 多状态时，每个 State 都允许有自己的主参数；后面会按“单主参数可分组 / 多主参数强制嵌套”分流。
            var analysisResult = AnalyzeTransitionConditions(entryTrans, commonConditionParameters);
            if (!analysisResult.Success)
            {
                result.RejectReason = analysisResult.Reason;
                return result;
            }

            // MA Responsive/Overlay 这类图层常见 State -> Exit。
            // 旧逻辑会因为 ExitTime/Duration 直接拒绝；这里仅拒绝真正跳到其它 State 的转换，Exit 转换会被忽略。
            foreach (var t in state.Transitions)
            {
                if (t.DestinationState != null)
                {
                    result.RejectReason = $"State \"{state.Name}\" has outgoing transition to another state";
                    return result;
                }
            }

            stateInfos.Add(new StateInfo
            {
                IsDefault = false,
                Order = stateOrder,
                StateName = state.Name,
                ParameterName = analysisResult.ParameterName,
                IsInverted = analysisResult.IsInverted,
                ThresholdLo = analysisResult.ThresholdLo,
                ThresholdHi = analysisResult.ThresholdHi,
                Motion = state.Motion,
                SecondaryConditions = analysisResult.SecondaryConditions ?? new List<SecondaryCondition>()
            });
        }

        var mainParams = stateInfos
            .Where(s => !s.IsDefault)
            .Select(s => s.ParameterName)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct()
            .ToList();

        if (mainParams.Count == 0)
        {
            result.RejectReason = "Failed to extract parameter name";
            return result;
        }

        result.IsConvertible = true;
        result.MainParameterNames = mainParams;
        result.RequiresNestedLayerTree = mainParams.Count > 1;
        result.ParameterName = result.RequiresNestedLayerTree ? null : mainParams[0];
        result.IsInverted = !result.RequiresNestedLayerTree && stateInfos
            .Where(s => !s.IsDefault)
            .All(s => s.IsInverted);
        result.States = stateInfos;
        return result;
    }

    List<VirtualStateMachine.VirtualChildState> OrderConditionalStatesByEntryTransitions(
        List<VirtualStateMachine.VirtualChildState> conditionalStates,
        IEnumerable<VirtualTransition> entryTransitions,
        object defaultState)
    {
        var ordered = new List<VirtualStateMachine.VirtualChildState>();

        // Unity 会按 Entry Transition 列表的顺序决定多个 Entry 分支的优先级。
        // 之前使用 m_ChildStates 的显示顺序，会导致“更具体条件”的状态被后面的普通状态覆盖。
        foreach (var transition in entryTransitions)
        {
            var destination = transition.DestinationState;
            if (destination == null || ReferenceEquals(destination, defaultState))
                continue;

            if (ordered.Any(cs => ReferenceEquals(cs.State, destination)))
                continue;

            foreach (var cs in conditionalStates)
            {
                if (ReferenceEquals(cs.State, destination))
                {
                    ordered.Add(cs);
                    break;
                }
            }
        }

        // 极少数情况下，如果某个状态没有出现在 EntryTransitions 的遍历结果中，保留原状态列表顺序作为 fallback。
        foreach (var cs in conditionalStates)
        {
            if (!ordered.Any(existing => ReferenceEquals(existing.State, cs.State)))
                ordered.Add(cs);
        }

        return ordered;
    }

    HashSet<string> GetCommonConditionParameters(IEnumerable<List<VirtualTransition>> transitionsByState)
    {
        HashSet<(string ParameterName, float Lo, float Hi)> common = null;
        int stateCount = 0;

        foreach (var transitions in transitionsByState)
        {
            var stateRanges = GetConditionRanges(transitions);

            stateCount++;
            if (common == null)
            {
                common = stateRanges;
            }
            else
            {
                common.IntersectWith(stateRanges);
            }
        }

        return stateCount > 1 && common != null
            ? common.Select(r => r.ParameterName).ToHashSet()
            : new HashSet<string>();
    }

    HashSet<(string ParameterName, float Lo, float Hi)> GetConditionRanges(List<VirtualTransition> transitions)
    {
        var result = new HashSet<(string ParameterName, float Lo, float Hi)>();
        if (transitions == null) return result;

        foreach (var group in transitions
            .SelectMany(t => t.Conditions)
            .Where(c => !string.IsNullOrEmpty(c.parameter))
            .GroupBy(c => c.parameter))
        {
            float lo = float.NegativeInfinity;
            float hi = float.PositiveInfinity;
            bool supported = true;

            foreach (var cond in group)
            {
                switch (cond.mode)
                {
                    case AnimatorConditionMode.Greater:
                        lo = Math.Max(lo, cond.threshold);
                        break;
                    case AnimatorConditionMode.Less:
                        hi = Math.Min(hi, cond.threshold);
                        break;
                    case AnimatorConditionMode.Equals:
                        if (!_fx.Parameters.TryGetValue(cond.parameter, out var equalsParameter) ||
                            equalsParameter.type != AnimatorControllerParameterType.Int ||
                            !IsNearlyInteger(cond.threshold))
                        {
                            supported = false;
                            break;
                        }

                        int equalsValue = Mathf.RoundToInt(cond.threshold);
                        lo = Math.Max(lo, equalsValue - 0.5f);
                        hi = Math.Min(hi, equalsValue + 0.5f);
                        break;
                    case AnimatorConditionMode.If:
                        lo = Math.Max(lo, 0.5f);
                        break;
                    case AnimatorConditionMode.IfNot:
                        hi = Math.Min(hi, 0.5f);
                        break;
                    default:
                        supported = false;
                        break;
                }
            }

            if (supported && lo < hi)
                result.Add((group.Key, lo, hi));
        }

        return result;
    }

    TransitionAnalysisResult AnalyzeTransitionConditions(
        List<VirtualTransition> entryTransitions,
        HashSet<string> commonConditionParameters)
    {

        if (entryTransitions.Count == 1)
        {
            var conditions = entryTransitions[0].Conditions;
            return AnalyzeSingleTransitionConditions(conditions, false, commonConditionParameters);
        }
        else
        {
            var allConditions = new List<AnimatorCondition>();
            foreach (var t in entryTransitions)
            {
                if (t.Conditions.Count != 1)
                {
                    return TransitionAnalysisResult.Fail(
                        $"In inverted mode, Entry Transition has {t.Conditions.Count} conditions (expected 1)");
                }
                allConditions.Add(t.Conditions[0]);
            }

            var paramNames = allConditions.Select(c => c.parameter).Distinct().ToList();
            if (paramNames.Count != 1)
            {
                return TransitionAnalysisResult.Fail(
                    $"Multiple parameters in inverted mode: {string.Join(", ", paramNames)}");
            }

            var invertedConditions = new List<AnimatorCondition>();
            foreach (var condition in allConditions)
            {
                if (!TryInvertCondition(condition, out var invertedCondition))
                {
                    return TransitionAnalysisResult.Fail(
                        $"Unsupported inverted condition mode: {condition.mode}");
                }

                invertedConditions.Add(invertedCondition);
            }

            return AnalyzeSingleTransitionConditions(invertedConditions.ToImmutableList(), true, commonConditionParameters);
        }
    }

    bool TryInvertCondition(AnimatorCondition condition, out AnimatorCondition inverted)
    {
        inverted = new AnimatorCondition
        {
            parameter = condition.parameter,
            threshold = condition.threshold
        };

        switch (condition.mode)
        {
            case AnimatorConditionMode.Greater:
                inverted.mode = AnimatorConditionMode.Less;
                return true;
            case AnimatorConditionMode.Less:
                inverted.mode = AnimatorConditionMode.Greater;
                return true;
            case AnimatorConditionMode.If:
                inverted.mode = AnimatorConditionMode.IfNot;
                return true;
            case AnimatorConditionMode.IfNot:
                inverted.mode = AnimatorConditionMode.If;
                return true;
            default:
                return false;
        }
    }

    TransitionAnalysisResult AnalyzeSingleTransitionConditions(
        ImmutableList<AnimatorCondition> conditions,
        bool isInverted,
        HashSet<string> commonConditionParameters)
    {
        if (conditions.Count == 0)
            return TransitionAnalysisResult.Fail("No conditions");

        if (isInverted && conditions.Select(c => c.parameter).Distinct().Count() != 1)
        {
            return TransitionAnalysisResult.Fail(
                "Inverted multi-parameter AND conditions are not supported");
        }

        var groupedConditions = conditions.GroupBy(c => c.parameter).ToList();
        var parameterRanges = new List<(string ParameterName, float Lo, float Hi)>();

        foreach (var group in groupedConditions)
        {
            float lo = float.NegativeInfinity;
            float hi = float.PositiveInfinity;

            foreach (var cond in group)
            {
                switch (cond.mode)
                {
                    case AnimatorConditionMode.Greater:
                        lo = Math.Max(lo, cond.threshold);
                        break;
                    case AnimatorConditionMode.Less:
                        hi = Math.Min(hi, cond.threshold);
                        break;
                    case AnimatorConditionMode.Equals:
                        if (!_fx.Parameters.TryGetValue(cond.parameter, out var equalsParameter) ||
                            equalsParameter.type != AnimatorControllerParameterType.Int)
                        {
                            return TransitionAnalysisResult.Fail(
                                $"Equals condition requires Int parameter, got {equalsParameter?.type.ToString() ?? "Missing"}");
                        }

                        if (!IsNearlyInteger(cond.threshold))
                        {
                            return TransitionAnalysisResult.Fail(
                                $"Int Equals threshold must be integer, got {cond.threshold}");
                        }

                        int equalsValue = Mathf.RoundToInt(cond.threshold);
                        lo = Math.Max(lo, equalsValue - 0.5f);
                        hi = Math.Min(hi, equalsValue + 0.5f);
                        break;
                    case AnimatorConditionMode.NotEqual:
                        return TransitionAnalysisResult.Fail(
                            $"Unsupported condition mode: {cond.mode}");
                    case AnimatorConditionMode.If:
                        lo = Math.Max(lo, 0.5f);
                        break;
                    case AnimatorConditionMode.IfNot:
                        hi = Math.Min(hi, 0.5f);
                        break;
                }
            }

            if (lo >= hi)
            {
                return TransitionAnalysisResult.Fail(
                    $"Invalid condition range for parameter \"{group.Key}\": {lo}..{hi}");
            }

            parameterRanges.Add((group.Key, lo, hi));
        }

        var mainRange = PreferNonCommonParameters(parameterRanges
            .Where(r => !IsActiveSelfProxyParameter(r.ParameterName)
                && float.IsFinite(r.Lo)
                && float.IsFinite(r.Hi)), commonConditionParameters)
            .OrderByDescending(r => r.Hi - r.Lo)
            .FirstOrDefault();

        if (mainRange.ParameterName == null)
        {
            mainRange = PreferNonCommonParameters(parameterRanges
                .Where(r => float.IsFinite(r.Lo) && float.IsFinite(r.Hi))
                , commonConditionParameters)
                .OrderBy(r => IsActiveSelfProxyParameter(r.ParameterName) ? 1 : 0)
                .ThenByDescending(r => r.Hi - r.Lo)
                .FirstOrDefault();
        }

        if (mainRange.ParameterName == null)
        {
            mainRange = PreferNonCommonParameters(parameterRanges
                .Where(r => float.IsFinite(r.Lo) || float.IsFinite(r.Hi))
                , commonConditionParameters)
                .OrderBy(r => IsActiveSelfProxyParameter(r.ParameterName) ? 1 : 0)
                .FirstOrDefault();
        }

        if (mainRange.ParameterName == null)
        {
            return TransitionAnalysisResult.Fail("Failed to identify main parameter");
        }

        var secondaryConditions = new List<SecondaryCondition>();
        foreach (var range in parameterRanges)
        {
            if (range.ParameterName == mainRange.ParameterName) continue;

            bool hasLower = float.IsFinite(range.Lo);
            bool hasUpper = float.IsFinite(range.Hi);
            if (hasLower == hasUpper)
            {
                return TransitionAnalysisResult.Fail(
                    $"Secondary parameter \"{range.ParameterName}\" must be a simple boolean condition");
            }

            secondaryConditions.Add(new SecondaryCondition
            {
                ParameterName = range.ParameterName,
                ActiveWhenGreater = hasLower,
                Threshold = hasLower ? range.Lo : range.Hi
            });
        }

        return new TransitionAnalysisResult
        {
            Success = true,
            ParameterName = mainRange.ParameterName,
            ThresholdLo = mainRange.Lo,
            ThresholdHi = mainRange.Hi,
            IsInverted = isInverted,
            SecondaryConditions = secondaryConditions
        };
    }

    IEnumerable<(string ParameterName, float Lo, float Hi)> PreferNonCommonParameters(
        IEnumerable<(string ParameterName, float Lo, float Hi)> ranges,
        HashSet<string> commonConditionParameters)
    {
        var list = ranges.ToList();
        if (list.Count <= 1 || commonConditionParameters == null || commonConditionParameters.Count == 0)
            return list;

        var nonCommon = list
            .Where(r => !commonConditionParameters.Contains(r.ParameterName))
            .ToList();

        return nonCommon.Count > 0 ? nonCommon : list;
    }

    struct TransitionAnalysisResult
    {
        public bool Success;
        public string Reason;
        public string ParameterName;
        public float ThresholdLo;
        public float ThresholdHi;
        public bool IsInverted;
        public List<SecondaryCondition> SecondaryConditions;

        public static TransitionAnalysisResult Fail(string reason) =>
            new TransitionAnalysisResult { Success = false, Reason = reason };
    }

    struct ManualConditionInfo
    {
        public string ParameterName;
        public AnimatorControllerParameterType ParameterType;
        public float ThresholdLo;
        public float ThresholdHi;
        public AnimatorConditionMode Mode;
        public float RawThreshold;
        public bool IsDiscreteEquals;
        public int DiscreteValue;
    }

    AnalyzedLayer AnalyzeManualSimpleLayer(VirtualLayer layer, int index, string originalRejectReason)
    {
        var result = new AnalyzedLayer
        {
            Layer = layer,
            OriginalIndex = index,
            IsConvertible = false,
            IsExternalLayer = true
        };

        if (!_settings.scanAllLayers)
        {
            result.RejectReason = "Manual simple layer scan requires Scan All Layers";
            return result;
        }

        var sm = layer.StateMachine;
        if (sm == null)
        {
            result.RejectReason = "No state machine";
            return result;
        }

        if (StateMachineHasBehaviours(sm, out var stateMachineBehaviourSummary))
        {
            result.RejectReason = $"StateMachine 存在行为组件：{stateMachineBehaviourSummary}";
            return result;
        }

        if (sm.StateMachines != null && sm.StateMachines.Count > 0)
        {
            result.RejectReason = "Manual simple layer does not support sub state machines";
            return result;
        }

        var childStates = sm.States?.ToList() ?? new List<VirtualStateMachine.VirtualChildState>();
        if (childStates.Count < 2)
        {
            result.RejectReason = $"Manual simple layer requires at least 2 states ({childStates.Count})";
            return result;
        }

        var defaultState = sm.DefaultState;
        if (defaultState == null)
        {
            result.RejectReason = "No default state";
            return result;
        }

        if (!childStates.Any(cs => SameVirtualState(cs.State, defaultState)))
        {
            result.RejectReason = "Default state is not in root state list";
            return result;
        }

        foreach (var childState in childStates)
        {
            var state = childState.State;
            var stateName = state?.Name ?? "<Unnamed>";

            if (state == null)
            {
                result.RejectReason = "Manual simple layer contains null state";
                return result;
            }

            if (HasExcludedPrefix(stateName, _settings.excludedStatePrefixes, out var matchedStatePrefix))
            {
                result.RejectReason = $"Excluded by state prefix: {matchedStatePrefix} (state \"{stateName}\")";
                return result;
            }

            if (StateHasBehaviours(state, out var behaviourSummary))
            {
                result.RejectReason = $"状态 \"{stateName}\" 存在行为组件：{behaviourSummary}";
                return result;
            }

            if (!ManualStateUsesDefaultSettings(state, out var stateSettingReason))
            {
                result.RejectReason = $"状态 \"{stateName}\" 使用了非默认设置：{stateSettingReason}";
                return result;
            }
        }

        if (TryAnalyzeManualAnyStateMultiValueLayer(layer, index, childStates, defaultState, out var anyStateMultiAnalyzed, out var anyStateMultiRejectReason))
            return anyStateMultiAnalyzed;

        string anyStateRejectReason = null;
        if (childStates.Count == 2 && TryAnalyzeManualAnyStateLayer(layer, index, childStates, defaultState, out var anyStateAnalyzed, out anyStateRejectReason))
            return anyStateAnalyzed;

        string bidirectionalRejectReason = null;
        if (childStates.Count == 2 && TryAnalyzeManualBidirectionalLayer(layer, index, childStates, defaultState, out var bidirectionalAnalyzed, out bidirectionalRejectReason))
            return bidirectionalAnalyzed;

        if (childStates.Count != 2)
        {
            if (anyStateRejectReason == null) anyStateRejectReason = "two-state AnyState fallback requires exactly 2 states";
            if (bidirectionalRejectReason == null) bidirectionalRejectReason = "bidirectional fallback requires exactly 2 states";
        }

        result.RejectReason = $"Manual simple layer pattern not matched. AnyStateMulti: {anyStateMultiRejectReason}; AnyState: {anyStateRejectReason}; Bidirectional: {bidirectionalRejectReason}";
        return result;
    }

    bool TryAnalyzeManualAnyStateMultiValueLayer(
        VirtualLayer layer,
        int index,
        List<VirtualStateMachine.VirtualChildState> childStates,
        VirtualState defaultState,
        out AnalyzedLayer analyzed,
        out string rejectReason)
    {
        analyzed = null;
        rejectReason = null;

        var sm = layer.StateMachine;
        var states = childStates.Select(cs => cs.State).Where(s => s != null).ToList();
        var conditionalStates = states.Where(s => !SameVirtualState(s, defaultState)).ToList();
        if (conditionalStates.Count == 0)
        {
            rejectReason = "No non-default states";
            return false;
        }

        if (conditionalStates.Count > 1 && !_settings.convertMultiState)
        {
            rejectReason = $"Manual AnyState multi-value layer has {conditionalStates.Count} conditional states, enable Multi-State Layers";
            return false;
        }

        var anyStateTransitions = sm.AnyStateTransitions?.ToList() ?? new List<VirtualStateTransition>();
        if (anyStateTransitions.Count == 0)
        {
            rejectReason = "No AnyState transitions";
            return false;
        }

        foreach (var state in states)
        {
            if (state.Transitions != null && state.Transitions.Count > 0)
            {
                rejectReason = $"State \"{state.Name}\" has outgoing transition";
                return false;
            }
        }

        var entries = new List<(VirtualState State, ManualConditionInfo Condition, int Order)>();
        var targetedStates = new HashSet<VirtualState>();
        var usedValues = new HashSet<int>();
        string parameterName = null;

        for (int i = 0; i < anyStateTransitions.Count; i++)
        {
            var transition = anyStateTransitions[i];
            var destination = transition.DestinationState;
            if (destination == null)
            {
                rejectReason = "AnyState transition destination is null";
                return false;
            }

            if (SameVirtualState(destination, defaultState))
            {
                rejectReason = "Manual AnyState multi-value layer uses default state as fallback; AnyState transitions to the default state are not supported";
                return false;
            }

            if (!states.Any(s => SameVirtualState(s, destination)))
            {
                rejectReason = $"AnyState transition targets state outside root state list: {destination.Name}";
                return false;
            }

            if (!ManualTransitionUsesSafeSettings(transition, out var transitionReason))
            {
                rejectReason = $"AnyState -> \"{destination.Name}\" transition is unsafe: {transitionReason}";
                return false;
            }

            if (!TryAnalyzeManualSimpleCondition(transition.Conditions, out var condition, out var conditionReason))
            {
                rejectReason = $"Failed to analyze AnyState -> \"{destination.Name}\" condition: {conditionReason}";
                return false;
            }

            if (condition.ParameterType != AnimatorControllerParameterType.Int || !condition.IsDiscreteEquals)
            {
                rejectReason = "Manual AnyState multi-value layer currently supports Int Equals conditions only";
                return false;
            }

            if (parameterName == null) parameterName = condition.ParameterName;
            if (parameterName != condition.ParameterName)
            {
                rejectReason = $"Manual AnyState multi-value layer uses multiple parameters: {parameterName}, {condition.ParameterName}";
                return false;
            }

            if (!usedValues.Add(condition.DiscreteValue))
            {
                rejectReason = $"Duplicate Int Equals value: {condition.DiscreteValue}";
                return false;
            }

            if (targetedStates.Any(s => SameVirtualState(s, destination)))
            {
                rejectReason = $"Multiple AnyState transitions target the same state: {destination.Name}";
                return false;
            }

            targetedStates.Add(destination);
            entries.Add((destination, condition, i));
        }

        foreach (var state in conditionalStates)
        {
            if (!targetedStates.Any(s => SameVirtualState(s, state)))
            {
                rejectReason = $"Non-default state \"{state.Name}\" has no AnyState Int Equals transition";
                return false;
            }
        }

        analyzed = BuildManualAnyStateMultiValueAnalyzedLayer(
            layer,
            index,
            defaultState,
            entries);

        return true;
    }

    AnalyzedLayer BuildManualAnyStateMultiValueAnalyzedLayer(
        VirtualLayer layer,
        int index,
        VirtualState defaultState,
        List<(VirtualState State, ManualConditionInfo Condition, int Order)> entries)
    {
        var states = new List<StateInfo>
        {
            new StateInfo
            {
                IsDefault = true,
                Order = -1,
                StateName = defaultState.Name,
                Motion = defaultState.Motion
            }
        };

        foreach (var entry in entries.OrderBy(e => e.Order))
        {
            states.Add(new StateInfo
            {
                IsDefault = false,
                Order = entry.Order,
                StateName = entry.State.Name,
                ParameterName = entry.Condition.ParameterName,
                IsInverted = false,
                ThresholdLo = entry.Condition.ThresholdLo,
                ThresholdHi = entry.Condition.ThresholdHi,
                Motion = entry.State.Motion,
                SecondaryConditions = new List<SecondaryCondition>()
            });
        }

        string parameterName = entries.First().Condition.ParameterName;

        return new AnalyzedLayer
        {
            Layer = layer,
            OriginalIndex = index,
            IsConvertible = true,
            RejectReason = null,
            ParameterName = parameterName,
            IsInverted = false,
            States = states,
            IsExternalLayer = true,
            HasMultipleConditionalStates = entries.Count > 1,
            RequiresNestedLayerTree = false,
            MainParameterNames = new List<string> { parameterName }
        };
    }

    bool TryAnalyzeManualAnyStateLayer(
        VirtualLayer layer,
        int index,
        List<VirtualStateMachine.VirtualChildState> childStates,
        VirtualState defaultState,
        out AnalyzedLayer analyzed,
        out string rejectReason)
    {
        analyzed = null;
        rejectReason = null;

        var sm = layer.StateMachine;
        var states = childStates.Select(cs => cs.State).Where(s => s != null).ToList();
        var nonDefaultState = states.FirstOrDefault(s => !SameVirtualState(s, defaultState));
        if (nonDefaultState == null)
        {
            rejectReason = "No non-default state";
            return false;
        }

        if (sm.AnyStateTransitions == null || sm.AnyStateTransitions.Count != 2)
        {
            rejectReason = $"AnyState transition count must be 2 ({sm.AnyStateTransitions?.Count ?? 0})";
            return false;
        }

        foreach (var state in states)
        {
            if (state.Transitions != null && state.Transitions.Count > 0)
            {
                rejectReason = $"State \"{state.Name}\" has outgoing transition";
                return false;
            }
        }

        var transitionToDefault = sm.AnyStateTransitions.FirstOrDefault(t => SameVirtualState(t.DestinationState, defaultState));
        var transitionToNonDefault = sm.AnyStateTransitions.FirstOrDefault(t => SameVirtualState(t.DestinationState, nonDefaultState));

        if (transitionToDefault == null || transitionToNonDefault == null)
        {
            rejectReason = "AnyState transitions must target both states";
            return false;
        }

        if (!ManualTransitionUsesSafeSettings(transitionToDefault, out var defaultTransitionReason))
        {
            rejectReason = $"AnyState -> \"{defaultState.Name}\" transition is unsafe: {defaultTransitionReason}";
            return false;
        }

        if (!ManualTransitionUsesSafeSettings(transitionToNonDefault, out var nonDefaultTransitionReason))
        {
            rejectReason = $"AnyState -> \"{nonDefaultState.Name}\" transition is unsafe: {nonDefaultTransitionReason}";
            return false;
        }

        if (!TryAnalyzeManualSimpleCondition(transitionToDefault.Conditions, out var defaultCondition, out var defaultConditionReason))
        {
            rejectReason = $"Failed to analyze default state condition: {defaultConditionReason}";
            return false;
        }

        if (!TryAnalyzeManualSimpleCondition(transitionToNonDefault.Conditions, out var nonDefaultCondition, out var nonDefaultConditionReason))
        {
            rejectReason = $"Failed to analyze non-default state condition: {nonDefaultConditionReason}";
            return false;
        }

        if (!ManualConditionsCanFormTwoStateSwitch(defaultCondition, nonDefaultCondition, out var compatibilityReason))
        {
            rejectReason = compatibilityReason;
            return false;
        }

        analyzed = BuildManualSimpleAnalyzedLayer(
            layer,
            index,
            defaultState,
            nonDefaultState,
            nonDefaultCondition);

        return true;
    }

    bool TryAnalyzeManualBidirectionalLayer(
        VirtualLayer layer,
        int index,
        List<VirtualStateMachine.VirtualChildState> childStates,
        VirtualState defaultState,
        out AnalyzedLayer analyzed,
        out string rejectReason)
    {
        analyzed = null;
        rejectReason = null;

        var sm = layer.StateMachine;
        var states = childStates.Select(cs => cs.State).Where(s => s != null).ToList();
        var nonDefaultState = states.FirstOrDefault(s => !SameVirtualState(s, defaultState));
        if (nonDefaultState == null)
        {
            rejectReason = "No non-default state";
            return false;
        }

        if (sm.AnyStateTransitions != null && sm.AnyStateTransitions.Count > 0)
        {
            rejectReason = "Bidirectional pattern does not allow AnyState transitions";
            return false;
        }

        var defaultTransitions = defaultState.Transitions?.Where(t => t.DestinationState != null).ToList() ?? new List<VirtualStateTransition>();
        var nonDefaultTransitions = nonDefaultState.Transitions?.Where(t => t.DestinationState != null).ToList() ?? new List<VirtualStateTransition>();

        var defaultOutgoing = defaultTransitions
            .Where(t => SameVirtualState(t.DestinationState, nonDefaultState))
            .ToList();
        var nonDefaultOutgoing = nonDefaultTransitions
            .Where(t => SameVirtualState(t.DestinationState, defaultState))
            .ToList();

        if (defaultOutgoing.Count != 1 || nonDefaultOutgoing.Count != 1)
        {
            rejectReason = $"Bidirectional transitions must be exactly 1 each ({defaultOutgoing.Count}, {nonDefaultOutgoing.Count})";
            return false;
        }

        if (defaultTransitions.Count != 1)
        {
            rejectReason = $"Default state \"{defaultState.Name}\" has unsupported outgoing transitions";
            return false;
        }

        if (nonDefaultTransitions.Count != 1)
        {
            rejectReason = $"Non-default state \"{nonDefaultState.Name}\" has unsupported outgoing transitions";
            return false;
        }

        var toNonDefault = defaultOutgoing[0];
        var toDefault = nonDefaultOutgoing[0];

        if (!ManualTransitionUsesSafeSettings(toNonDefault, out var toNonDefaultTransitionReason))
        {
            rejectReason = $"\"{defaultState.Name}\" -> \"{nonDefaultState.Name}\" transition is unsafe: {toNonDefaultTransitionReason}";
            return false;
        }

        if (!ManualTransitionUsesSafeSettings(toDefault, out var toDefaultTransitionReason))
        {
            rejectReason = $"\"{nonDefaultState.Name}\" -> \"{defaultState.Name}\" transition is unsafe: {toDefaultTransitionReason}";
            return false;
        }

        if (!TryAnalyzeManualSimpleCondition(toNonDefault.Conditions, out var nonDefaultCondition, out var nonDefaultConditionReason))
        {
            rejectReason = $"Failed to analyze non-default state condition: {nonDefaultConditionReason}";
            return false;
        }

        if (!TryAnalyzeManualSimpleCondition(toDefault.Conditions, out var defaultCondition, out var defaultConditionReason))
        {
            rejectReason = $"Failed to analyze default state condition: {defaultConditionReason}";
            return false;
        }

        if (!ManualConditionsCanFormTwoStateSwitch(defaultCondition, nonDefaultCondition, out var compatibilityReason))
        {
            rejectReason = compatibilityReason;
            return false;
        }

        analyzed = BuildManualSimpleAnalyzedLayer(
            layer,
            index,
            defaultState,
            nonDefaultState,
            nonDefaultCondition);

        return true;
    }

    AnalyzedLayer BuildManualSimpleAnalyzedLayer(
        VirtualLayer layer,
        int index,
        VirtualState defaultState,
        VirtualState nonDefaultState,
        ManualConditionInfo nonDefaultCondition)
    {
        var states = new List<StateInfo>
        {
            new StateInfo
            {
                IsDefault = true,
                Order = -1,
                StateName = defaultState.Name,
                Motion = defaultState.Motion
            },
            new StateInfo
            {
                IsDefault = false,
                Order = 0,
                StateName = nonDefaultState.Name,
                ParameterName = nonDefaultCondition.ParameterName,
                IsInverted = false,
                ThresholdLo = nonDefaultCondition.ThresholdLo,
                ThresholdHi = nonDefaultCondition.ThresholdHi,
                Motion = nonDefaultState.Motion,
                SecondaryConditions = new List<SecondaryCondition>()
            }
        };

        return new AnalyzedLayer
        {
            Layer = layer,
            OriginalIndex = index,
            IsConvertible = true,
            RejectReason = null,
            ParameterName = nonDefaultCondition.ParameterName,
            IsInverted = false,
            States = states,
            IsExternalLayer = true,
            HasMultipleConditionalStates = false,
            RequiresNestedLayerTree = false,
            MainParameterNames = new List<string> { nonDefaultCondition.ParameterName }
        };
    }

    bool SameVirtualState(VirtualState a, VirtualState b)
    {
        return ReferenceEquals(a, b) || a == b;
    }

    bool TryAnalyzeManualSimpleCondition(
        IEnumerable<AnimatorCondition> conditions,
        out ManualConditionInfo conditionInfo,
        out string reason)
    {
        conditionInfo = default;
        reason = null;

        var conditionList = conditions?.ToList() ?? new List<AnimatorCondition>();
        if (conditionList.Count != 1)
        {
            reason = $"Manual simple transition requires exactly 1 condition ({conditionList.Count})";
            return false;
        }

        var condition = conditionList[0];
        if (string.IsNullOrEmpty(condition.parameter))
        {
            reason = "Condition has empty parameter";
            return false;
        }

        if (!_fx.Parameters.TryGetValue(condition.parameter, out var parameter))
        {
            reason = $"Parameter \"{condition.parameter}\" not found in FX controller";
            return false;
        }

        float lo = float.NegativeInfinity;
        float hi = float.PositiveInfinity;
        bool isDiscreteEquals = false;
        int discreteValue = 0;

        switch (condition.mode)
        {
            case AnimatorConditionMode.If:
                if (parameter.type != AnimatorControllerParameterType.Bool)
                {
                    reason = $"If condition requires Bool parameter, got {parameter.type}";
                    return false;
                }
                lo = 0.5f;
                break;

            case AnimatorConditionMode.IfNot:
                if (parameter.type != AnimatorControllerParameterType.Bool)
                {
                    reason = $"IfNot condition requires Bool parameter, got {parameter.type}";
                    return false;
                }
                hi = 0.5f;
                break;

            case AnimatorConditionMode.Greater:
                if (parameter.type != AnimatorControllerParameterType.Float &&
                    parameter.type != AnimatorControllerParameterType.Int)
                {
                    reason = $"Greater condition requires Float or Int parameter, got {parameter.type}";
                    return false;
                }
                lo = condition.threshold;
                break;

            case AnimatorConditionMode.Less:
                if (parameter.type != AnimatorControllerParameterType.Float &&
                    parameter.type != AnimatorControllerParameterType.Int)
                {
                    reason = $"Less condition requires Float or Int parameter, got {parameter.type}";
                    return false;
                }
                hi = condition.threshold;
                break;

            case AnimatorConditionMode.Equals:
                if (parameter.type != AnimatorControllerParameterType.Int)
                {
                    reason = $"Equals condition is only supported for Int parameters in manual simple layers, got {parameter.type}";
                    return false;
                }

                if (!IsNearlyInteger(condition.threshold))
                {
                    reason = $"Int Equals threshold must be integer, got {condition.threshold}";
                    return false;
                }

                discreteValue = Mathf.RoundToInt(condition.threshold);
                if (discreteValue < 0)
                {
                    reason = $"Manual Int Equals only supports non-negative values, got {discreteValue}";
                    return false;
                }

                lo = discreteValue - 0.5f;
                hi = discreteValue + 0.5f;
                isDiscreteEquals = true;
                break;

            case AnimatorConditionMode.NotEqual:
                reason = "NotEqual is not supported in manual simple layers";
                return false;

            default:
                reason = $"Unsupported manual condition mode: {condition.mode}";
                return false;
        }

        if (lo >= hi)
        {
            reason = $"Invalid condition range: {lo}..{hi}";
            return false;
        }

        conditionInfo = new ManualConditionInfo
        {
            ParameterName = condition.parameter,
            ParameterType = parameter.type,
            ThresholdLo = lo,
            ThresholdHi = hi,
            Mode = condition.mode,
            RawThreshold = condition.threshold,
            IsDiscreteEquals = isDiscreteEquals,
            DiscreteValue = discreteValue
        };

        return true;
    }

    bool ManualConditionsCanFormTwoStateSwitch(
        ManualConditionInfo defaultCondition,
        ManualConditionInfo nonDefaultCondition,
        out string reason)
    {
        reason = null;

        if (defaultCondition.ParameterName != nonDefaultCondition.ParameterName)
        {
            reason = $"Manual switch conditions use different parameters: {defaultCondition.ParameterName}, {nonDefaultCondition.ParameterName}";
            return false;
        }

        if (defaultCondition.ParameterType != nonDefaultCondition.ParameterType)
        {
            reason = $"Manual switch conditions use different parameter types: {defaultCondition.ParameterType}, {nonDefaultCondition.ParameterType}";
            return false;
        }

        if (defaultCondition.IsDiscreteEquals || nonDefaultCondition.IsDiscreteEquals)
        {
            if (!defaultCondition.IsDiscreteEquals || !nonDefaultCondition.IsDiscreteEquals)
            {
                reason = "Manual Int Equals switch requires both sides to use Equals";
                return false;
            }

            if (!((defaultCondition.DiscreteValue == 0 && nonDefaultCondition.DiscreteValue == 1) ||
                  (defaultCondition.DiscreteValue == 1 && nonDefaultCondition.DiscreteValue == 0)))
            {
                reason = $"Manual Int Equals switch currently only supports complementary 0/1 values: {defaultCondition.DiscreteValue}, {nonDefaultCondition.DiscreteValue}";
                return false;
            }

            return true;
        }

        bool defaultLowerOnly = float.IsFinite(defaultCondition.ThresholdLo) && float.IsPositiveInfinity(defaultCondition.ThresholdHi);
        bool defaultUpperOnly = float.IsNegativeInfinity(defaultCondition.ThresholdLo) && float.IsFinite(defaultCondition.ThresholdHi);
        bool nonDefaultLowerOnly = float.IsFinite(nonDefaultCondition.ThresholdLo) && float.IsPositiveInfinity(nonDefaultCondition.ThresholdHi);
        bool nonDefaultUpperOnly = float.IsNegativeInfinity(nonDefaultCondition.ThresholdLo) && float.IsFinite(nonDefaultCondition.ThresholdHi);

        if (defaultCondition.ParameterType == AnimatorControllerParameterType.Bool)
        {
            if (defaultLowerOnly && nonDefaultUpperOnly && Mathf.Approximately(defaultCondition.ThresholdLo, nonDefaultCondition.ThresholdHi))
                return true;

            if (defaultUpperOnly && nonDefaultLowerOnly && Mathf.Approximately(defaultCondition.ThresholdHi, nonDefaultCondition.ThresholdLo))
                return true;

            reason = "Bool manual switch requires complementary true/false conditions";
            return false;
        }

        if (defaultCondition.ParameterType == AnimatorControllerParameterType.Float)
        {
            if (defaultLowerOnly && nonDefaultUpperOnly && Mathf.Approximately(defaultCondition.ThresholdLo, nonDefaultCondition.ThresholdHi))
                return true;

            if (defaultUpperOnly && nonDefaultLowerOnly && Mathf.Approximately(defaultCondition.ThresholdHi, nonDefaultCondition.ThresholdLo))
                return true;

            reason = "Float manual switch requires complementary Greater/Less conditions with the same threshold";
            return false;
        }

        if (defaultCondition.ParameterType == AnimatorControllerParameterType.Int)
        {
            if (defaultLowerOnly && nonDefaultUpperOnly && IsNearlyZero(defaultCondition.ThresholdLo) && Mathf.Approximately(nonDefaultCondition.ThresholdHi, 1f))
                return true;

            if (defaultUpperOnly && nonDefaultLowerOnly && Mathf.Approximately(defaultCondition.ThresholdHi, 1f) && IsNearlyZero(nonDefaultCondition.ThresholdLo))
                return true;

            reason = "Int manual switch supports Equals 0/1 or complementary Less 1 / Greater 0 only";
            return false;
        }

        reason = $"Unsupported parameter type: {defaultCondition.ParameterType}";
        return false;
    }

    bool ManualTransitionUsesSafeSettings(VirtualStateTransition transition, out string reason)
    {
        reason = null;
        if (transition == null)
        {
            reason = "transition is null";
            return false;
        }

        if (TryReadBoolMember(transition, out bool hasExitTime, "HasExitTime", "hasExitTime") && hasExitTime)
        {
            if (!TryReadFloatMember(transition, out float exitTime, "ExitTime", "exitTime"))
            {
                reason = "cannot read Exit Time";
                return false;
            }

            if (!IsNearlyZero(exitTime))
            {
                reason = $"Exit Time must be 0 when Has Exit Time is enabled, got {exitTime}";
                return false;
            }
        }

        if (!TryReadFloatMember(transition, out float duration, "Duration", "duration"))
        {
            reason = "cannot read Transition Duration";
            return false;
        }

        if (!IsNearlyZero(duration))
        {
            reason = $"Transition Duration must be 0, got {duration}";
            return false;
        }

        if (!TryReadFloatMember(transition, out float offset, "Offset", "offset"))
        {
            reason = "cannot read Transition Offset";
            return false;
        }

        if (!IsNearlyZero(offset))
        {
            reason = $"Transition Offset must be 0, got {offset}";
            return false;
        }

        if (TryReadMember(transition, out object interruptionSource, "InterruptionSource", "interruptionSource"))
        {
            if (interruptionSource != null && interruptionSource.ToString() != "None")
            {
                reason = $"Interruption Source must be None, got {interruptionSource}";
                return false;
            }
        }

        return true;
    }

    bool ManualStateUsesDefaultSettings(VirtualState state, out string reason)
    {
        reason = null;
        if (state == null)
        {
            reason = "state is null";
            return false;
        }

        if (TryReadFloatMember(state, out float speed, "Speed", "speed") && !Mathf.Approximately(speed, 1f))
        {
            reason = $"Speed must be 1, got {speed}";
            return false;
        }

        if (TryReadBoolMember(state, out bool speedParameterActive, "SpeedParameterActive", "speedParameterActive") && speedParameterActive)
        {
            reason = "Speed Multiplier parameter is enabled";
            return false;
        }

        if (TryReadBoolMember(state, out bool timeParameterActive, "TimeParameterActive", "timeParameterActive", "MotionTimeParameterActive", "motionTimeParameterActive") && timeParameterActive)
        {
            reason = "Motion Time parameter is enabled";
            return false;
        }

        if (TryReadBoolMember(state, out bool mirror, "Mirror", "mirror") && mirror)
        {
            reason = "Mirror must be disabled";
            return false;
        }

        if (TryReadBoolMember(state, out bool mirrorParameterActive, "MirrorParameterActive", "mirrorParameterActive") && mirrorParameterActive)
        {
            reason = "Mirror parameter is enabled";
            return false;
        }

        if (TryReadFloatMember(state, out float cycleOffset, "CycleOffset", "cycleOffset") && !IsNearlyZero(cycleOffset))
        {
            reason = $"Cycle Offset must be 0, got {cycleOffset}";
            return false;
        }

        if (TryReadBoolMember(state, out bool cycleOffsetParameterActive, "CycleOffsetParameterActive", "cycleOffsetParameterActive") && cycleOffsetParameterActive)
        {
            reason = "Cycle Offset parameter is enabled";
            return false;
        }

        if (TryReadBoolMember(state, out bool footIk, "IKOnFeet", "iKOnFeet", "FootIK", "footIK") && footIk)
        {
            reason = "Foot IK must be disabled";
            return false;
        }

        // Write Defaults 暂时不作为手动图层的拒绝条件。
        return true;
    }

    bool StateMachineHasBehaviours(VirtualStateMachine stateMachine, out string behaviourSummary)
    {
        behaviourSummary = null;
        if (stateMachine == null)
            return false;

        if (!TryReadMember(stateMachine, out object behavioursObject, "Behaviours", "behaviours"))
            return false;

        if (behavioursObject is not System.Collections.IEnumerable behavioursEnumerable)
            return false;

        var behaviours = behavioursEnumerable.Cast<object>().ToList();
        if (behaviours.Count == 0)
            return false;

        var behaviourNames = behaviours
            .Where(b => b != null)
            .Select(b => b.GetType().Name)
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        int missingCount = behaviours.Count(b => b == null);
        if (missingCount > 0)
            behaviourNames.Add($"Missing/Null x{missingCount}");

        behaviourSummary = behaviourNames.Count > 0
            ? string.Join(", ", behaviourNames)
            : $"{behaviours.Count} 个 StateMachineBehaviour";

        return true;
    }


    bool StateMachineHasBlockingBehaviours(VirtualStateMachine stateMachine, bool allowMAMMDLayerControl, out string behaviourSummary)
    {
        behaviourSummary = null;
        if (stateMachine == null)
            return false;

        if (!TryReadMember(stateMachine, out object behavioursObject, "Behaviours", "behaviours"))
            return false;

        if (behavioursObject is not System.Collections.IEnumerable behavioursEnumerable)
            return false;

        return HasBlockingBehaviours(behavioursEnumerable.Cast<object>(), allowMAMMDLayerControl, out behaviourSummary);
    }

    bool StateHasBlockingBehaviours(VirtualState state, bool allowMAMMDLayerControl, out string behaviourSummary)
    {
        behaviourSummary = null;
        if (state == null || state.Behaviours == null || state.Behaviours.Count == 0)
            return false;

        return HasBlockingBehaviours(state.Behaviours.Cast<object>(), allowMAMMDLayerControl, out behaviourSummary);
    }

    bool HasBlockingBehaviours(IEnumerable<object> behaviours, bool allowMAMMDLayerControl, out string behaviourSummary)
    {
        behaviourSummary = null;
        var behaviourList = behaviours?.ToList() ?? new List<object>();
        if (behaviourList.Count == 0)
            return false;

        var blockingBehaviours = behaviourList
            .Where(b => b == null || !(allowMAMMDLayerControl && IsMAMMDLayerControlBehaviour(b)))
            .ToList();

        if (blockingBehaviours.Count == 0)
            return false;

        var behaviourNames = blockingBehaviours
            .Where(b => b != null)
            .Select(b => b.GetType().Name)
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        int missingCount = blockingBehaviours.Count(b => b == null);
        if (missingCount > 0)
            behaviourNames.Add($"Missing/Null x{missingCount}");

        behaviourSummary = behaviourNames.Count > 0
            ? string.Join(", ", behaviourNames)
            : $"{blockingBehaviours.Count} 个 StateMachineBehaviour";

        return true;
    }

    bool IsMAMMDLayerControlBehaviour(object behaviour)
    {
        if (behaviour == null)
            return false;

        var type = behaviour.GetType();
        string text = $"{type.FullName} {type.Name} {behaviour}";
        text = text.Replace("_", "").Replace(" ", "").Replace("-", "");

        return text.IndexOf("MMDLayerControl", StringComparison.OrdinalIgnoreCase) >= 0
            || (text.IndexOf("MMD", StringComparison.OrdinalIgnoreCase) >= 0
                && text.IndexOf("Layer", StringComparison.OrdinalIgnoreCase) >= 0
                && text.IndexOf("Control", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    bool TryReadBoolMember(object target, out bool value, params string[] names)
    {
        value = false;
        if (!TryReadMember(target, out var rawValue, names))
            return false;

        if (rawValue is bool boolValue)
        {
            value = boolValue;
            return true;
        }

        return false;
    }

    bool TryReadFloatMember(object target, out float value, params string[] names)
    {
        value = 0f;
        if (!TryReadMember(target, out var rawValue, names))
            return false;

        try
        {
            value = Convert.ToSingle(rawValue);
            return true;
        }
        catch
        {
            return false;
        }
    }

    bool TryReadMember(object target, out object value, params string[] names)
    {
        value = null;
        if (target == null || names == null)
            return false;

        var type = target.GetType();
        foreach (var name in names)
        {
            if (string.IsNullOrEmpty(name))
                continue;

            var property = type.GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (property != null)
            {
                value = property.GetValue(target, null);
                return true;
            }

            var field = type.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (field != null)
            {
                value = field.GetValue(target);
                return true;
            }
        }

        return false;
    }

    bool IsNearlyZero(float value)
    {
        return Mathf.Abs(value) <= 0.0001f;
    }

    Dictionary<string, string> CollectPrefixProtectedParameters()
    {
        var protectedParameters = new Dictionary<string, string>();

        var layerPrefixes = _settings?.excludedLayerPrefixes ?? new List<string>();
        var statePrefixes = _settings?.excludedStatePrefixes ?? new List<string>();
        var parameterPrefixes = GetExcludedParameterPrefixes().ToList();

        foreach (var parameterName in _fx.Parameters.Keys)
        {
            if (HasExcludedPrefix(parameterName, parameterPrefixes, out var matchedParameterPrefix))
            {
                AddProtectedParameterReason(
                    protectedParameters,
                    parameterName,
                    $"命中参数名前缀 \"{matchedParameterPrefix}\"");
            }
        }

        foreach (var layer in _fx.Layers)
        {
            bool layerPrefixMatched = HasExcludedPrefix(layer.Name, layerPrefixes, out var matchedLayerPrefix);
            bool statePrefixMatched = LayerHasStateWithExcludedPrefix(layer, statePrefixes, out var matchedStateName, out var matchedStatePrefix);

            if (!layerPrefixMatched && !statePrefixMatched)
                continue;

            var reasonParts = new List<string>();
            if (layerPrefixMatched)
                reasonParts.Add($"图层 \"{layer.Name}\" 命中图层名前缀 \"{matchedLayerPrefix}\"");
            if (statePrefixMatched)
                reasonParts.Add($"状态 \"{matchedStateName}\" 命中状态名前缀 \"{matchedStatePrefix}\"");

            string reason = string.Join("；", reasonParts);
            foreach (var parameterName in CollectLayerConditionParameterNames(layer).OrderBy(p => p))
            {
                AddProtectedParameterReason(protectedParameters, parameterName, reason);
            }
        }

        return protectedParameters;
    }

    void AddProtectedParameterReason(Dictionary<string, string> protectedParameters, string parameterName, string reason)
    {
        if (protectedParameters == null || string.IsNullOrEmpty(parameterName) || string.IsNullOrEmpty(reason))
            return;

        if (!protectedParameters.TryGetValue(parameterName, out var existing) || string.IsNullOrEmpty(existing))
        {
            protectedParameters[parameterName] = reason;
            return;
        }

        if (!existing.Contains(reason))
            protectedParameters[parameterName] = existing + "；" + reason;
    }

    void ApplyPrefixProtectedParameterExclusions(List<AnalyzedLayer> analyzedLayers)
    {
        if (analyzedLayers == null || analyzedLayers.Count == 0 || _prefixProtectedParameterReasons.Count == 0)
            return;

        foreach (var analyzed in analyzedLayers.Where(l => l.IsConvertible))
        {
            var layerParameters = CollectLayerConditionParameterNames(analyzed.Layer)
                .Concat(GetAnalyzedLayerParameterNames(analyzed))
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct()
                .OrderBy(p => p)
                .ToList();

            var protectedParameters = layerParameters
                .Where(_prefixProtectedParameterReasons.ContainsKey)
                .ToList();

            if (protectedParameters.Count == 0)
                continue;

            analyzed.IsConvertible = false;
            analyzed.RejectReason = "参数受排除前缀保护: " + string.Join("; ",
                protectedParameters.Select(p => $"参数 \"{p}\" {_prefixProtectedParameterReasons[p]}"));
        }
    }

    bool LayerHasStateWithExcludedPrefix(
        VirtualLayer layer,
        IEnumerable<string> statePrefixes,
        out string matchedStateName,
        out string matchedPrefix)
    {
        matchedStateName = null;
        matchedPrefix = null;

        if (layer?.StateMachine == null || statePrefixes == null)
            return false;

        return StateMachineHasStateWithExcludedPrefix(
            layer.StateMachine,
            statePrefixes,
            new HashSet<object>(),
            out matchedStateName,
            out matchedPrefix);
    }

    bool StateMachineHasStateWithExcludedPrefix(
        object stateMachine,
        IEnumerable<string> statePrefixes,
        HashSet<object> visited,
        out string matchedStateName,
        out string matchedPrefix)
    {
        matchedStateName = null;
        matchedPrefix = null;

        if (stateMachine == null || visited == null || visited.Contains(stateMachine))
            return false;

        visited.Add(stateMachine);

        if (TryReadMember(stateMachine, out object statesObject, "States", "states") &&
            statesObject is System.Collections.IEnumerable statesEnumerable)
        {
            foreach (var childState in statesEnumerable)
            {
                if (!TryReadMember(childState, out object stateObject, "State", "state") || stateObject == null)
                    continue;

                string stateName = null;
                if (TryReadMember(stateObject, out object stateNameObject, "Name", "name"))
                    stateName = stateNameObject?.ToString();

                if (HasExcludedPrefix(stateName, statePrefixes, out matchedPrefix))
                {
                    matchedStateName = stateName;
                    return true;
                }
            }
        }

        if (TryReadMember(stateMachine, out object childStateMachinesObject, "StateMachines", "stateMachines") &&
            childStateMachinesObject is System.Collections.IEnumerable childStateMachinesEnumerable)
        {
            foreach (var childStateMachine in childStateMachinesEnumerable)
            {
                object nestedStateMachine = childStateMachine;
                if (TryReadMember(childStateMachine, out object readNestedStateMachine, "StateMachine", "stateMachine") &&
                    readNestedStateMachine != null)
                {
                    nestedStateMachine = readNestedStateMachine;
                }

                if (StateMachineHasStateWithExcludedPrefix(
                        nestedStateMachine,
                        statePrefixes,
                        visited,
                        out matchedStateName,
                        out matchedPrefix))
                    return true;
            }
        }

        return false;
    }

    void ApplySharedBoolIntParameterProtection(List<AnalyzedLayer> analyzedLayers)
    {
        if (analyzedLayers == null || analyzedLayers.Count == 0)
            return;

        var allLayerParameters = _fx.Layers.ToDictionary(
            layer => layer,
            layer => CollectLayerConditionParameterNames(layer));

        var layerOrder = _fx.Layers
            .Select((layer, index) => new { Layer = layer, Index = index })
            .ToDictionary(x => x.Layer, x => x.Index);

        var analyzedByLayer = analyzedLayers
            .GroupBy(l => l.Layer)
            .ToDictionary(g => g.Key, g => g.First());

        var candidateLayers = new HashSet<VirtualLayer>(analyzedLayers
            .Where(l => l.IsConvertible)
            .Select(l => l.Layer));

        bool changed;
        do
        {
            changed = false;

            var candidateBoolIntParameters = candidateLayers
                .SelectMany(layer => allLayerParameters.TryGetValue(layer, out var parameters)
                    ? parameters
                    : Enumerable.Empty<string>())
                .Where(IsBoolOrIntParameter)
                .Distinct()
                .OrderBy(p => p)
                .ToList();

            if (candidateBoolIntParameters.Count == 0)
                break;

            var blockedParameters = new Dictionary<string, string>();

            foreach (var parameterName in candidateBoolIntParameters)
            {
                // 允许保留/受保护图层的条件也一起被改写。
                // 因此不再因为“受保护图层引用了同一个 Bool/Int 参数”而阻止迁移；
                // 只要整个 FX 控制器里所有相关条件都能等价改写为 Float 条件，就允许这个参数迁移。
                if (!CanRewriteParameterAsFloat(parameterName, out var rewriteReason))
                    blockedParameters[parameterName] = rewriteReason;
            }

            if (blockedParameters.Count == 0)
                continue;

            var layersToReject = candidateLayers
                .Where(layer => allLayerParameters.TryGetValue(layer, out var parameters) &&
                                parameters.Any(blockedParameters.ContainsKey))
                .OrderBy(layer => layerOrder.TryGetValue(layer, out var order) ? order : int.MaxValue)
                .ToList();

            foreach (var layer in layersToReject)
            {
                candidateLayers.Remove(layer);
                changed = true;

                if (!analyzedByLayer.TryGetValue(layer, out var analyzed))
                    continue;

                var layerParameters = allLayerParameters.TryGetValue(layer, out var parameters)
                    ? parameters
                    : new HashSet<string>();

                var reasons = layerParameters
                    .Where(blockedParameters.ContainsKey)
                    .Select(p => $"参数 \"{p}\" {blockedParameters[p]}")
                    .Distinct()
                    .ToList();

                analyzed.IsConvertible = false;
                analyzed.RejectReason = "Bool/Int 全局 Float 迁移失败: " + string.Join("; ", reasons);
            }
        } while (changed);
    }

    Dictionary<string, AnimatorControllerParameterType> GetMigratedBoolIntParameterTypes(List<AnalyzedLayer> convertibleLayers)
    {
        var result = new Dictionary<string, AnimatorControllerParameterType>();
        if (convertibleLayers == null)
            return result;

        foreach (var parameterName in convertibleLayers
            .SelectMany(GetAnalyzedLayerParameterNames)
            .Distinct()
            .OrderBy(p => p))
        {
            if (!_fx.Parameters.TryGetValue(parameterName, out var parameter))
                continue;

            if (!IsBoolOrIntParameterType(parameter.type))
                continue;

            result[parameterName] = parameter.type;
        }

        if (result.Count > 0)
        {
            Debug.Log("[MA2BT Pro] Bool/Int 参数将迁移为 Float：" +
                string.Join(", ", result.Select(kv => $"{kv.Key}({kv.Value})")));
        }

        return result;
    }

    IEnumerable<string> GetAnalyzedLayerParameterNames(AnalyzedLayer layer)
    {
        if (layer == null || layer.States == null)
            return Enumerable.Empty<string>();

        return layer.States
            .Where(s => !s.IsDefault)
            .SelectMany(s =>
            {
                var names = new List<string>();
                if (!string.IsNullOrEmpty(s.ParameterName))
                    names.Add(s.ParameterName);

                if (s.SecondaryConditions != null)
                {
                    names.AddRange(s.SecondaryConditions
                        .Select(c => c.ParameterName)
                        .Where(p => !string.IsNullOrEmpty(p)));
                }

                return names;
            })
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct();
    }

    bool IsBoolOrIntParameter(string parameterName)
    {
        return !string.IsNullOrEmpty(parameterName) &&
               _fx.Parameters.TryGetValue(parameterName, out var parameter) &&
               IsBoolOrIntParameterType(parameter.type);
    }

    bool IsBoolOrIntParameterType(AnimatorControllerParameterType type)
    {
        return type == AnimatorControllerParameterType.Bool ||
               type == AnimatorControllerParameterType.Int;
    }

    bool CanRewriteParameterAsFloat(string parameterName, out string reason)
    {
        reason = null;

        if (!_fx.Parameters.TryGetValue(parameterName, out var parameter))
            return true;

        if (!IsBoolOrIntParameterType(parameter.type))
            return true;

        foreach (var layer in _fx.Layers)
        {
            foreach (var transition in EnumerateLayerTransitions(layer))
            {
                if (!TryGetTransitionConditions(transition, out var conditions))
                    continue;

                foreach (var condition in conditions)
                {
                    if (condition.parameter != parameterName)
                        continue;

                    if (!CanConvertConditionToFloat(condition, parameter.type, out var conditionReason))
                    {
                        reason = $"无法安全迁移为 Float：图层 \"{layer.Name}\" 的条件 {DescribeCondition(condition)}，{conditionReason}";
                        return false;
                    }
                }
            }
        }

        return true;
    }

    bool CanConvertConditionToFloat(
        AnimatorCondition condition,
        AnimatorControllerParameterType originalType,
        out string reason)
    {
        reason = null;

        if (originalType == AnimatorControllerParameterType.Bool)
        {
            if (condition.mode == AnimatorConditionMode.If ||
                condition.mode == AnimatorConditionMode.IfNot)
                return true;

            reason = $"Bool 只支持 If / IfNot，当前为 {condition.mode}";
            return false;
        }

        if (originalType == AnimatorControllerParameterType.Int)
        {
            switch (condition.mode)
            {
                case AnimatorConditionMode.Greater:
                case AnimatorConditionMode.Less:
                    return true;

                case AnimatorConditionMode.Equals:
                    if (!IsNearlyInteger(condition.threshold))
                    {
                        reason = $"Int Equals 阈值必须是整数，当前为 {condition.threshold}";
                        return false;
                    }
                    return true;

                case AnimatorConditionMode.NotEqual:
                    reason = "Int NotEqual 不支持等价迁移";
                    return false;

                default:
                    reason = $"Int 不支持 {condition.mode} 条件迁移";
                    return false;
            }
        }

        return true;
    }

    void RewriteFloatMigratedParameterConditions(
        Dictionary<string, AnimatorControllerParameterType> migratedParameterTypes,
        HashSet<VirtualLayer> layersToRemove)
    {
        if (migratedParameterTypes == null || migratedParameterTypes.Count == 0)
            return;

        int rewrittenTransitionCount = 0;
        int rewrittenConditionCount = 0;

        foreach (var layer in _fx.Layers)
        {
            if (layersToRemove != null && layersToRemove.Contains(layer))
                continue;

            foreach (var transition in EnumerateLayerTransitions(layer))
            {
                if (!TryGetTransitionConditions(transition, out var conditions))
                    continue;

                bool changed = false;
                var rewrittenConditions = new List<AnimatorCondition>();

                foreach (var condition in conditions)
                {
                    if (!migratedParameterTypes.TryGetValue(condition.parameter, out var originalType))
                    {
                        rewrittenConditions.Add(condition);
                        continue;
                    }

                    var converted = ConvertConditionToFloatConditions(condition, originalType);
                    rewrittenConditions.AddRange(converted);
                    rewrittenConditionCount += converted.Count;
                    changed = true;
                }

                if (!changed)
                    continue;

                if (TrySetTransitionConditions(transition, rewrittenConditions, out var setReason))
                {
                    rewrittenTransitionCount++;
                }
                else
                {
                    Debug.LogWarning($"[MA2BT Pro] 无法重写保留图层中的过渡条件：{setReason}");
                }
            }
        }

        if (rewrittenTransitionCount > 0)
        {
            Debug.Log($"[MA2BT Pro] 已重写 {rewrittenTransitionCount} 个保留过渡、{rewrittenConditionCount} 个 Bool/Int 条件为 Float 条件。");
        }
    }

    List<AnimatorCondition> ConvertConditionToFloatConditions(
        AnimatorCondition condition,
        AnimatorControllerParameterType originalType)
    {
        if (originalType == AnimatorControllerParameterType.Bool)
        {
            if (condition.mode == AnimatorConditionMode.If)
            {
                return new List<AnimatorCondition>
                {
                    MakeFloatCondition(condition.parameter, AnimatorConditionMode.Greater, 0.5f)
                };
            }

            if (condition.mode == AnimatorConditionMode.IfNot)
            {
                return new List<AnimatorCondition>
                {
                    MakeFloatCondition(condition.parameter, AnimatorConditionMode.Less, 0.5f)
                };
            }

            return new List<AnimatorCondition> { condition };
        }

        if (originalType == AnimatorControllerParameterType.Int)
        {
            switch (condition.mode)
            {
                case AnimatorConditionMode.Greater:
                    return new List<AnimatorCondition>
                    {
                        MakeFloatCondition(condition.parameter, AnimatorConditionMode.Greater, condition.threshold + 0.5f)
                    };

                case AnimatorConditionMode.Less:
                    return new List<AnimatorCondition>
                    {
                        MakeFloatCondition(condition.parameter, AnimatorConditionMode.Less, condition.threshold - 0.5f)
                    };

                case AnimatorConditionMode.Equals:
                    int value = Mathf.RoundToInt(condition.threshold);
                    return new List<AnimatorCondition>
                    {
                        MakeFloatCondition(condition.parameter, AnimatorConditionMode.Greater, value - 0.5f),
                        MakeFloatCondition(condition.parameter, AnimatorConditionMode.Less, value + 0.5f)
                    };
            }
        }

        return new List<AnimatorCondition> { condition };
    }

    AnimatorCondition MakeFloatCondition(string parameter, AnimatorConditionMode mode, float threshold)
    {
        return new AnimatorCondition
        {
            parameter = parameter,
            mode = mode,
            threshold = threshold
        };
    }

    string DescribeCondition(AnimatorCondition condition)
    {
        return $"{condition.parameter} {condition.mode} {condition.threshold}";
    }

    HashSet<string> CollectLayerConditionParameterNames(VirtualLayer layer)
    {
        var result = new HashSet<string>();
        if (layer?.StateMachine == null)
            return result;

        CollectStateMachineConditionParameterNames(layer.StateMachine, result, new HashSet<object>());
        return result;
    }

    void CollectStateMachineConditionParameterNames(
        object stateMachine,
        HashSet<string> result,
        HashSet<object> visited)
    {
        if (stateMachine == null || result == null || visited == null || visited.Contains(stateMachine))
            return;

        visited.Add(stateMachine);

        foreach (var transition in EnumerateTransitionsFromObject(stateMachine, "EntryTransitions", "entryTransitions", "AnyStateTransitions", "anyStateTransitions"))
            CollectTransitionConditionParameterNames(transition, result);

        if (TryReadMember(stateMachine, out object statesObject, "States", "states") &&
            statesObject is System.Collections.IEnumerable statesEnumerable)
        {
            foreach (var childState in statesEnumerable)
            {
                if (!TryReadMember(childState, out object state, "State", "state") || state == null)
                    continue;

                foreach (var transition in EnumerateTransitionsFromObject(state, "Transitions", "transitions"))
                    CollectTransitionConditionParameterNames(transition, result);
            }
        }

        if (TryReadMember(stateMachine, out object childMachinesObject, "StateMachines", "stateMachines") &&
            childMachinesObject is System.Collections.IEnumerable childMachinesEnumerable)
        {
            foreach (var childMachine in childMachinesEnumerable)
            {
                if (TryReadMember(childMachine, out object nestedMachine, "StateMachine", "stateMachine") && nestedMachine != null)
                    CollectStateMachineConditionParameterNames(nestedMachine, result, visited);
            }
        }
    }

    void CollectTransitionConditionParameterNames(object transition, HashSet<string> result)
    {
        if (transition == null || result == null)
            return;

        if (!TryGetTransitionConditions(transition, out var conditions))
            return;

        foreach (var condition in conditions)
        {
            if (!string.IsNullOrEmpty(condition.parameter))
                result.Add(condition.parameter);
        }
    }

    IEnumerable<object> EnumerateLayerTransitions(VirtualLayer layer)
    {
        if (layer?.StateMachine == null)
            yield break;

        foreach (var transition in EnumerateStateMachineTransitions(layer.StateMachine, new HashSet<object>()))
            yield return transition;
    }

    IEnumerable<object> EnumerateStateMachineTransitions(object stateMachine, HashSet<object> visited)
    {
        if (stateMachine == null || visited == null || visited.Contains(stateMachine))
            yield break;

        visited.Add(stateMachine);

        foreach (var transition in EnumerateTransitionsFromObject(stateMachine, "EntryTransitions", "entryTransitions", "AnyStateTransitions", "anyStateTransitions"))
            yield return transition;

        if (TryReadMember(stateMachine, out object statesObject, "States", "states") &&
            statesObject is System.Collections.IEnumerable statesEnumerable)
        {
            foreach (var childState in statesEnumerable)
            {
                if (!TryReadMember(childState, out object state, "State", "state") || state == null)
                    continue;

                foreach (var transition in EnumerateTransitionsFromObject(state, "Transitions", "transitions"))
                    yield return transition;
            }
        }

        if (TryReadMember(stateMachine, out object childMachinesObject, "StateMachines", "stateMachines") &&
            childMachinesObject is System.Collections.IEnumerable childMachinesEnumerable)
        {
            foreach (var childMachine in childMachinesEnumerable)
            {
                if (!TryReadMember(childMachine, out object nestedMachine, "StateMachine", "stateMachine") || nestedMachine == null)
                    continue;

                foreach (var transition in EnumerateStateMachineTransitions(nestedMachine, visited))
                    yield return transition;
            }
        }
    }

    IEnumerable<object> EnumerateTransitionsFromObject(object owner, params string[] memberNames)
    {
        if (owner == null || memberNames == null)
            yield break;

        foreach (var memberName in memberNames)
        {
            if (!TryReadMember(owner, out object transitionsObject, memberName))
                continue;

            if (transitionsObject is not System.Collections.IEnumerable transitionsEnumerable)
                continue;

            foreach (var transition in transitionsEnumerable)
            {
                if (transition != null)
                    yield return transition;
            }
        }
    }

    bool TryGetTransitionConditions(object transition, out List<AnimatorCondition> conditions)
    {
        conditions = new List<AnimatorCondition>();
        if (transition == null)
            return false;

        if (!TryReadMember(transition, out object conditionsObject, "Conditions", "conditions"))
            return false;

        if (conditionsObject is not System.Collections.IEnumerable conditionsEnumerable)
            return false;

        foreach (var rawCondition in conditionsEnumerable)
        {
            if (rawCondition is AnimatorCondition condition)
                conditions.Add(condition);
        }

        return true;
    }

    bool TrySetTransitionConditions(
        object transition,
        List<AnimatorCondition> conditions,
        out string reason)
    {
        reason = null;
        if (transition == null)
        {
            reason = "transition is null";
            return false;
        }

        var type = transition.GetType();
        foreach (var name in new[] { "Conditions", "conditions" })
        {
            var property = type.GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (property != null && property.CanWrite)
            {
                if (!TryCreateCompatibleConditionCollection(conditions, property.PropertyType, out var converted))
                {
                    reason = $"cannot convert condition collection to {property.PropertyType}";
                    return false;
                }

                property.SetValue(transition, converted, null);
                return true;
            }

            var field = type.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (field != null && !field.IsInitOnly)
            {
                if (!TryCreateCompatibleConditionCollection(conditions, field.FieldType, out var converted))
                {
                    reason = $"cannot convert condition collection to {field.FieldType}";
                    return false;
                }

                field.SetValue(transition, converted);
                return true;
            }
        }

        reason = "Conditions member is not writable";
        return false;
    }

    bool TryCreateCompatibleConditionCollection(
        List<AnimatorCondition> conditions,
        Type targetType,
        out object converted)
    {
        converted = null;
        if (targetType == null)
            return false;

        var immutableConditions = (conditions ?? new List<AnimatorCondition>()).ToImmutableList();
        var listConditions = conditions ?? new List<AnimatorCondition>();
        var arrayConditions = listConditions.ToArray();

        if (targetType.IsInstanceOfType(immutableConditions))
        {
            converted = immutableConditions;
            return true;
        }

        if (targetType.IsInstanceOfType(listConditions))
        {
            converted = listConditions;
            return true;
        }

        if (targetType.IsInstanceOfType(arrayConditions))
        {
            converted = arrayConditions;
            return true;
        }

        return false;
    }


    #endregion

    #region 分组

    List<ParameterGroup> GroupByParameter(List<AnalyzedLayer> layers)
    {
        var groups = new Dictionary<string, ParameterGroup>();

        foreach (var layer in layers.OrderBy(l => l.OriginalIndex))
        {
            if (!groups.TryGetValue(layer.ParameterName, out var group))
            {
                group = new ParameterGroup { ParameterName = layer.ParameterName };
                groups[layer.ParameterName] = group;
            }
            group.Layers.Add(layer);
        }

        foreach (var group in groups.Values)
        {
            ComputeThresholds(group);
            foreach (var layer in group.Layers)
                EnsureParametersForLayer(layer);
        }

        return groups.Values.ToList();
    }


    List<NestedLayerGroup> GroupNestedLayers(List<AnalyzedLayer> layers)
    {
        var result = new List<NestedLayerGroup>();

        if (layers == null || layers.Count == 0)
            return result;

        if (!_settings.mergeIdenticalBlendTreesAndAnimations)
        {
            foreach (var layer in layers.OrderBy(l => l.OriginalIndex))
            {
                result.Add(new NestedLayerGroup
                {
                    Signature = $"layer:{layer.OriginalIndex}",
                    Layers = new List<AnalyzedLayer> { layer }
                });
            }
            return result;
        }

        var groups = new Dictionary<string, NestedLayerGroup>();
        foreach (var layer in layers.OrderBy(l => l.OriginalIndex))
        {
            string signature = GetNestedLayerSignature(layer);
            if (!groups.TryGetValue(signature, out var group))
            {
                group = new NestedLayerGroup { Signature = signature };
                groups.Add(signature, group);
            }
            group.Layers.Add(layer);
        }

        return groups.Values.OrderBy(g => g.OriginalIndex).ToList();
    }

    void EnsureParametersForLayer(AnalyzedLayer layer)
    {
        foreach (var paramName in layer.States
            .Where(s => !s.IsDefault)
            .Select(s => s.ParameterName)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct())
        {
            EnsureFloatParameter(paramName);
        }

        foreach (var secondaryParam in layer.States
            .SelectMany(s => s.SecondaryConditions ?? new List<SecondaryCondition>())
            .Select(c => c.ParameterName)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct())
        {
            EnsureFloatParameter(secondaryParam);
        }
    }

    void ComputeThresholds(ParameterGroup group)
    {
        var valueSet = new HashSet<float>();

        foreach (var layer in group.Layers)
        {
            foreach (var state in layer.States)
            {
                if (state.IsDefault) continue;
                AddRoundedIntegerThresholdSamples(valueSet, state.ParameterName, state.ThresholdLo, state.ThresholdHi);
            }
        }

        valueSet.RemoveWhere(v => v < 0 || float.IsNaN(v) || float.IsInfinity(v));

        if (valueSet.Count == 0)
        {
            // 没有任何可采样阈值时才保留 0，避免生成空的 1D BlendTree。
            valueSet.Add(0);
        }

        if (_settings.compactMode)
        {
            // Compact Mode: 只保留真正会改变 Motion 的整数采样点，以及每个有效区间左右各一个 Empty guard。
            // 例如只有 50 有动画时，只生成 49, 50, 51；不会再额外生成 0 -> Empty。
            // 例如 1, 2, 100 会变成 0, 1, 2, 3, 99, 100, 101，而不会生成 4..98 的空状态。
            group.Thresholds = valueSet.Distinct().OrderBy(x => x).ToList();
        }
        else
        {
            // 非 Compact Mode: 生成“首个保护阈值”到“最后一个保护阈值”之间的完整整数表。
            // 这样 50 会生成 49, 50, 51；1 和 100 会生成 0..101。
            // 不再强制从 0 开始，因为 1D BlendTree 会在参数小于第一个 threshold 时使用第一个 child。
            int min = (int)Math.Max(0, valueSet.Min());
            int max = (int)Math.Max(min, valueSet.Max());
            group.Thresholds = Enumerable.Range(min, max - min + 1)
                .Select(i => (float)i)
                .Union(valueSet)
                .Distinct()
                .OrderBy(x => x)
                .ToList();
        }
    }

    void AddRoundedIntegerThresholdSamples(HashSet<float> valueSet, string parameterName, float lo, float hi)
    {
        foreach (var sample in BuildRoundedIntegerSamples(parameterName, lo, hi))
            valueSet.Add(sample.Threshold);
    }

    #endregion

    #region 混合树生成

    VirtualBlendTree BuildRootBlendTree(List<ParameterGroup> paramGroups, List<NestedLayerGroup> nestedGroups)
    {
        var rootTree = VirtualBlendTree.Create("RootBlendTree");
        rootTree.BlendType = BlendTreeType.Direct;
        rootTree.BlendParameter = ROOT_PARAM;
        rootTree.BlendParameterY = ROOT_PARAM;

        var rootChildren = new List<(int Order, VirtualMotion Motion)>();

        foreach (var group in paramGroups)
        {
            var paramTree = BuildParameterBlendTree(group);
            int order = group.Layers.Count == 0 ? int.MaxValue : group.Layers.Min(l => l.OriginalIndex);
            rootChildren.Add((order, paramTree));
        }

        foreach (var group in nestedGroups)
        {
            var layerTree = BuildNestedLayerBlendTree(group);
            if (layerTree != null)
                rootChildren.Add((group.OriginalIndex, layerTree));
        }

        foreach (var child in rootChildren.OrderBy(c => c.Order))
        {
            rootTree.Children = rootTree.Children.Add(
                new VirtualBlendTree.VirtualChildMotion
                {
                    Motion = child.Motion,
                    DirectBlendParameter = ROOT_PARAM
                });
        }

        return rootTree;
    }

    VirtualMotion BuildNestedLayerBlendTree(NestedLayerGroup group)
    {
        if (group == null || group.Layers.Count == 0)
            return GetSharedEmptyClip();

        if (!_settings.mergeIdenticalBlendTreesAndAnimations || group.Layers.Count == 1)
            return BuildNestedLayerBlendTree(group.Layers[0]);

        return BuildMergedNestedLayerBlendTree(group);
    }

    VirtualMotion BuildMergedNestedLayerBlendTree(NestedLayerGroup group)
    {
        var prototypeLayer = group.Layers[0];
        var defaultState = prototypeLayer.States.FirstOrDefault(s => s.IsDefault);
        if (defaultState == null)
            return GetSharedEmptyClip();

        string namePrefix = SanitizeName(group.DisplayName);
        var defaultMotion = MergeMotionsIntoSingleMotion(
            group.Layers
                .Select(l => l.States.FirstOrDefault(s => s.IsDefault)?.Motion)
                .Where(m => m != null),
            $"{namePrefix}_Default");

        // 以第一个图层作为结构模板。GroupNestedLayers() 已经用签名保证所有图层的参数、状态数量、阈值和二级条件一致。
        return BuildNestedLayerStateChain(
            GetConditionalStatesByPriority(prototypeLayer),
            defaultMotion,
            state => MergeMotionsIntoSingleMotion(
                group.Layers
                    .Select(l => l.States.FirstOrDefault(s => !s.IsDefault && s.Order == state.Order)?.Motion)
                    .Where(m => m != null),
                $"{namePrefix}_state{state.Order}"),
            namePrefix);
    }

    VirtualMotion BuildNestedLayerBlendTree(AnalyzedLayer layer)
    {
        var defaultState = layer.States.FirstOrDefault(s => s.IsDefault);
        if (defaultState == null)
            return GetSharedEmptyClip();

        string namePrefix = SanitizeName(layer.Layer.Name);
        var defaultMotion = NormalizeEmptyMotion(defaultState.Motion);

        return BuildNestedLayerStateChain(
            GetConditionalStatesByPriority(layer),
            defaultMotion,
            state => NormalizeEmptyMotion(state.Motion),
            namePrefix);
    }

    List<StateInfo> GetConditionalStatesByPriority(AnalyzedLayer layer)
    {
        return layer.States
            .Where(s => !s.IsDefault)
            .OrderBy(s => s.Order)
            .ToList();
    }

    VirtualMotion BuildNestedLayerStateChain(
        List<StateInfo> conditionalStates,
        VirtualMotion defaultMotion,
        Func<StateInfo, VirtualMotion> getStateMotion,
        string namePrefix)
    {
        var commonConditionRanges = GetCommonConditionRanges(conditionalStates);
        if (commonConditionRanges.Count > 0)
        {
            var commonKeys = commonConditionRanges
                .Select(GetConditionRangeKey)
                .ToHashSet();

            var strippedStates = conditionalStates
                .Select(state => CloneStateWithoutCommonConditionRanges(state, commonKeys))
                .ToList();

            var activeMotion = BuildNestedLayerStateChainWithoutCommonSecondary(
                strippedStates,
                defaultMotion,
                getStateMotion,
                namePrefix);

            return BuildConditionRangeTree(
                activeMotion,
                defaultMotion ?? GetSharedEmptyClip(),
                commonConditionRanges,
                $"{namePrefix}_common");
        }

        var commonSecondaryConditions = GetCommonSecondaryConditions(conditionalStates);
        if (commonSecondaryConditions.Count > 0)
        {
            var commonKeys = commonSecondaryConditions
                .Select(GetSecondaryConditionKey)
                .ToHashSet();

            var strippedStates = conditionalStates
                .Select(state => CloneStateWithoutCommonSecondaryConditions(state, commonKeys))
                .ToList();

            var activeMotion = BuildNestedLayerStateChainWithoutCommonSecondary(
                strippedStates,
                defaultMotion,
                getStateMotion,
                namePrefix);

            return BuildSecondaryConditionTree(
                activeMotion,
                defaultMotion ?? GetSharedEmptyClip(),
                commonSecondaryConditions,
                $"{namePrefix}_common_sec");
        }

        return BuildNestedLayerStateChainWithoutCommonSecondary(
            conditionalStates,
            defaultMotion,
            getStateMotion,
            namePrefix);
    }

    VirtualMotion BuildNestedLayerStateChainWithoutCommonSecondary(
        List<StateInfo> conditionalStates,
        VirtualMotion defaultMotion,
        Func<StateInfo, VirtualMotion> getStateMotion,
        string namePrefix)
    {
        VirtualMotion current = defaultMotion ?? GetSharedEmptyClip();

        // Order 越小，表示 Entry Transition 越靠前，也就是优先级越高。
        // 生成嵌套树时要反向构建：先构建低优先级状态作为内层 fallback，
        // 再用高优先级状态包住它。这样同一参数值命中多个状态时，
        // 下方/更高优先级状态会覆盖上方/低优先级状态；条件不满足时才继续落到下一层。
        for (int i = conditionalStates.Count - 1; i >= 0; i--)
        {
            var state = conditionalStates[i];
            var fallbackMotion = current ?? defaultMotion ?? GetSharedEmptyClip();
            var stateMotion = getStateMotion(state) ?? GetSharedEmptyClip();

            if (state.SecondaryConditions != null && state.SecondaryConditions.Count > 0)
            {
                stateMotion = BuildSecondaryConditionTree(
                    stateMotion,
                    fallbackMotion,
                    state.SecondaryConditions,
                    $"{namePrefix}_state{state.Order}_sec");
            }

            if (!HasMainCondition(state))
            {
                current = stateMotion;
                continue;
            }

            current = BuildStateMainConditionTree(
                state,
                stateMotion,
                fallbackMotion,
                fallbackMotion,
                $"{namePrefix}_state{state.Order}");
        }

        return current ?? defaultMotion ?? GetSharedEmptyClip();
    }

    bool HasMainCondition(StateInfo state)
    {
        return state != null && !string.IsNullOrEmpty(state.ParameterName);
    }

    List<ConditionRange> GetCommonConditionRanges(List<StateInfo> conditionalStates)
    {
        if (conditionalStates == null || conditionalStates.Count <= 1)
            return new List<ConditionRange>();

        var firstStateConditions = GetStateConditionRanges(conditionalStates[0]);
        if (firstStateConditions.Count == 0)
            return new List<ConditionRange>();

        var common = firstStateConditions
            .GroupBy(GetConditionRangeKey)
            .ToDictionary(g => g.Key, g => g.First());

        for (int i = 1; i < conditionalStates.Count; i++)
        {
            var stateMap = GetStateConditionRanges(conditionalStates[i])
                .GroupBy(GetConditionRangeKey)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var key in common.Keys.ToList())
            {
                if (!stateMap.TryGetValue(key, out var matchedCondition))
                {
                    common.Remove(key);
                    continue;
                }

                common[key].AddSafetyGuardForOpenRange &=
                    matchedCondition.AddSafetyGuardForOpenRange;
            }

            if (common.Count == 0)
                return new List<ConditionRange>();
        }

        return firstStateConditions
            .Where(c => common.ContainsKey(GetConditionRangeKey(c)))
            .Select(CloneConditionRange)
            .ToList();
    }

    List<ConditionRange> GetStateConditionRanges(StateInfo state)
    {
        var ranges = new List<ConditionRange>();
        if (state == null || state.IsDefault)
            return ranges;

        if (HasMainCondition(state))
        {
            ranges.Add(new ConditionRange
            {
                ParameterName = state.ParameterName,
                ThresholdLo = state.ThresholdLo,
                ThresholdHi = state.ThresholdHi,
                IsInverted = state.IsInverted,
                AddSafetyGuardForOpenRange = false
            });
        }

        foreach (var condition in state.SecondaryConditions ?? new List<SecondaryCondition>())
        {
            var range = ConditionRangeFromSecondaryCondition(condition);
            if (range != null)
                ranges.Add(range);
        }

        return ranges;
    }

    StateInfo CloneStateWithoutCommonConditionRanges(StateInfo state, HashSet<string> commonConditionKeys)
    {
        var mainCondition = HasMainCondition(state)
            ? new ConditionRange
            {
                ParameterName = state.ParameterName,
                ThresholdLo = state.ThresholdLo,
                ThresholdHi = state.ThresholdHi,
                IsInverted = state.IsInverted,
                AddSafetyGuardForOpenRange = false
            }
            : null;

        bool stripMainCondition = mainCondition != null
            && commonConditionKeys.Contains(GetConditionRangeKey(mainCondition));

        return new StateInfo
        {
            IsDefault = state.IsDefault,
            Order = state.Order,
            StateName = state.StateName,
            ParameterName = stripMainCondition ? null : state.ParameterName,
            IsInverted = stripMainCondition ? false : state.IsInverted,
            ThresholdLo = stripMainCondition ? float.NaN : state.ThresholdLo,
            ThresholdHi = stripMainCondition ? float.NaN : state.ThresholdHi,
            Motion = state.Motion,
            SecondaryConditions = (state.SecondaryConditions ?? new List<SecondaryCondition>())
                .Where(c => !commonConditionKeys.Contains(GetConditionRangeKey(ConditionRangeFromSecondaryCondition(c))))
                .Select(CloneSecondaryCondition)
                .ToList()
        };
    }

    ConditionRange ConditionRangeFromSecondaryCondition(SecondaryCondition condition)
    {
        if (condition == null)
            return null;

        return new ConditionRange
        {
            ParameterName = condition.ParameterName,
            ThresholdLo = condition.ActiveWhenGreater ? condition.Threshold : float.NegativeInfinity,
            ThresholdHi = condition.ActiveWhenGreater ? float.PositiveInfinity : condition.Threshold,
            IsInverted = false,
            AddSafetyGuardForOpenRange = true
        };
    }

    ConditionRange CloneConditionRange(ConditionRange condition)
    {
        if (condition == null)
            return null;

        return new ConditionRange
        {
            ParameterName = condition.ParameterName,
            ThresholdLo = condition.ThresholdLo,
            ThresholdHi = condition.ThresholdHi,
            IsInverted = condition.IsInverted,
            AddSafetyGuardForOpenRange = condition.AddSafetyGuardForOpenRange
        };
    }

    string GetConditionRangeKey(ConditionRange condition)
    {
        if (condition == null) return "";
        return $"{condition.ParameterName}|{condition.ThresholdLo:R}|{condition.ThresholdHi:R}|{condition.IsInverted}";
    }

    List<SecondaryCondition> GetCommonSecondaryConditions(List<StateInfo> conditionalStates)
    {
        if (conditionalStates == null || conditionalStates.Count <= 1)
            return new List<SecondaryCondition>();

        Dictionary<string, SecondaryCondition> common = null;
        foreach (var state in conditionalStates)
        {
            var conditions = state.SecondaryConditions ?? new List<SecondaryCondition>();
            var stateMap = conditions
                .GroupBy(GetSecondaryConditionKey)
                .ToDictionary(g => g.Key, g => g.First());

            if (common == null)
            {
                common = stateMap;
            }
            else
            {
                foreach (var key in common.Keys.ToList())
                {
                    if (!stateMap.ContainsKey(key))
                        common.Remove(key);
                }
            }
        }

        if (common == null || common.Count == 0)
            return new List<SecondaryCondition>();

        return conditionalStates[0].SecondaryConditions
            .Where(c => common.ContainsKey(GetSecondaryConditionKey(c)))
            .Select(CloneSecondaryCondition)
            .ToList();
    }

    StateInfo CloneStateWithoutCommonSecondaryConditions(StateInfo state, HashSet<string> commonSecondaryKeys)
    {
        return new StateInfo
        {
            IsDefault = state.IsDefault,
            Order = state.Order,
            StateName = state.StateName,
            ParameterName = state.ParameterName,
            IsInverted = state.IsInverted,
            ThresholdLo = state.ThresholdLo,
            ThresholdHi = state.ThresholdHi,
            Motion = state.Motion,
            SecondaryConditions = (state.SecondaryConditions ?? new List<SecondaryCondition>())
                .Where(c => !commonSecondaryKeys.Contains(GetSecondaryConditionKey(c)))
                .Select(CloneSecondaryCondition)
                .ToList()
        };
    }

    SecondaryCondition CloneSecondaryCondition(SecondaryCondition condition)
    {
        return new SecondaryCondition
        {
            ParameterName = condition.ParameterName,
            ActiveWhenGreater = condition.ActiveWhenGreater,
            Threshold = condition.Threshold
        };
    }

    string GetSecondaryConditionKey(SecondaryCondition condition)
    {
        if (condition == null) return "";
        return $"{condition.ParameterName}|{condition.ActiveWhenGreater}|{condition.Threshold:R}";
    }

    VirtualMotion BuildStateMainConditionTree(
        StateInfo state,
        VirtualMotion activeMotion,
        VirtualMotion lowerInactiveMotion,
        VirtualMotion upperInactiveMotion,
        string name)
    {
        return BuildRoundedIntegerConditionTree(
            state.ParameterName,
            state.ThresholdLo,
            state.ThresholdHi,
            state.IsInverted,
            activeMotion,
            lowerInactiveMotion,
            upperInactiveMotion,
            $"{name}_{SanitizeName(state.ParameterName)}");
    }

    VirtualMotion BuildRoundedIntegerConditionTree(
        string parameterName,
        float lo,
        float hi,
        bool isInverted,
        VirtualMotion activeMotion,
        VirtualMotion lowerInactiveMotion,
        VirtualMotion upperInactiveMotion,
        string name,
        bool addSafetyGuardForOpenRange = false)
    {
        var samples = BuildRoundedIntegerSamples(parameterName, lo, hi, addSafetyGuardForOpenRange);
        if (samples.Count == 0)
            return isInverted ? lowerInactiveMotion : activeMotion;

        var tree = VirtualBlendTree.Create(name);
        tree.BlendType = BlendTreeType.Simple1D;
        tree.BlendParameter = parameterName;
        tree.UseAutomaticThresholds = false;

        void AddChild(VirtualMotion motion, float threshold)
        {
            tree.Children = tree.Children.Add(
                new VirtualBlendTree.VirtualChildMotion
                {
                    Motion = NormalizeEmptyMotion(motion),
                    Threshold = threshold
                });
        }

        foreach (var sample in samples)
        {
            VirtualMotion motion;

            // 安全隔离点必须使用真正的 Empty。
            // 例如未知连续参数的二级条件 “Param > 0.5” 可能会生成：
            // 0 -> 原 fallback，1 -> 条件动画，2 -> Empty。
            // 之前 2 会错误地继续使用 fallback，导致安全隔离不生效。
            if (sample.IsSafetyGuard)
            {
                motion = GetSharedEmptyClip();
            }
            else if (!isInverted)
            {
                motion = sample.Kind == RoundedSampleKind.Active
                    ? activeMotion
                    : sample.Kind == RoundedSampleKind.LowerInactive
                        ? lowerInactiveMotion
                        : upperInactiveMotion;
            }
            else
            {
                // inverted 模式表示“命中原范围时不进入此状态，未命中时进入此状态”。
                // 中间命中点走 fallback，两侧走 activeMotion。
                motion = sample.Kind == RoundedSampleKind.Active
                    ? lowerInactiveMotion
                    : activeMotion;
            }

            if (!sample.IsSafetyGuard)
            {
                motion = SimplifyMotionForKnownParameterRange(
                    motion,
                    parameterName,
                    sample.HasDomainMin ? (int?)sample.DomainMin : null,
                    sample.HasDomainMax ? (int?)sample.DomainMax : null);
            }

            AddChild(motion, sample.Threshold);
        }

        return tree;
    }

    enum RoundedSampleKind
    {
        LowerInactive,
        Active,
        UpperInactive
    }

    struct RoundedSample
    {
        public float Threshold;
        public RoundedSampleKind Kind;

        // true 表示这是额外添加的“安全隔离”阈值。
        // 它不是原状态机语义里的 fallback，因此必须指向 Empty，不能指向普通 fallback 动画。
        public bool IsSafetyGuard;

        // 当前 child 在离散整数参数语义下能覆盖的范围。
        // 用于去掉嵌套树里已经被父级条件限制过的重复判断。
        public bool HasDomainMin;
        public int DomainMin;
        public bool HasDomainMax;
        public int DomainMax;
    }

    RoundedSample MakeSample(
        float threshold,
        RoundedSampleKind kind,
        int? domainMin,
        int? domainMax,
        bool isSafetyGuard = false)
    {
        return new RoundedSample
        {
            Threshold = threshold,
            Kind = kind,
            IsSafetyGuard = isSafetyGuard,
            HasDomainMin = domainMin.HasValue,
            DomainMin = domainMin.GetValueOrDefault(),
            HasDomainMax = domainMax.HasValue,
            DomainMax = domainMax.GetValueOrDefault()
        };
    }

    VirtualMotion SimplifyMotionForKnownParameterRange(
        VirtualMotion motion,
        string parameterName,
        int? knownMin,
        int? knownMax)
    {
        if (motion == null || string.IsNullOrEmpty(parameterName))
            return motion;

        if (!(motion is VirtualBlendTree tree))
            return motion;

        // 先处理“同参数 Simple1D”：如果父级已经把这个参数限制到只会命中一个 child，
        // 就直接把这一层同参数判断剪掉。
        if (tree.BlendType == BlendTreeType.Simple1D && tree.BlendParameter == parameterName)
        {
            var children = tree.Children
                .OrderBy(c => c.Threshold)
                .ToList();

            if (children.Count == 0)
                return motion;

            if (children.Count == 1)
                return SimplifyMotionForKnownParameterRange(children[0].Motion, parameterName, knownMin, knownMax);

            var candidates = new List<VirtualBlendTree.VirtualChildMotion>();
            for (int i = 0; i < children.Count; i++)
            {
                GetChildIntegerDomain(tree.BlendParameter, children, i, out int? childMin, out int? childMax);
                if (RangesIntersect(knownMin, knownMax, childMin, childMax))
                    candidates.Add(children[i]);
            }

            if (candidates.Count == 1)
            {
                // 父级树已经把同一个参数限制到只可能走这一个 child，
                // 直接返回 child，避免再次生成“同参数 99/100/101”这种重复判断树。
                _sameParameterNestedCheckPruned++;
                return SimplifyMotionForKnownParameterRange(candidates[0].Motion, parameterName, knownMin, knownMax);
            }

            // 如果没法整层剪掉，也继续向子 Motion 递归剪枝。
            // 这能处理 Simple1D -> Direct -> Simple1D(同参数) 这种结构。
            bool changed = false;
            var newChildren = ImmutableList<VirtualBlendTree.VirtualChildMotion>.Empty;
            foreach (var child in children)
            {
                var simplifiedChildMotion = SimplifyMotionForKnownParameterRange(child.Motion, parameterName, knownMin, knownMax);
                if (!ReferenceEquals(simplifiedChildMotion, child.Motion))
                    changed = true;
                var newChild = CloneChildMotion(child, simplifiedChildMotion);
                newChildren = newChildren.Add(newChild);
            }

            if (changed)
            {
                return CloneBlendTreeWithChildren(tree, newChildren);
            }

            return motion;
        }

        // 关键修复：之前只会在“当前 motion 本身就是同参数 Simple1D”时剪枝。
        // 但实际生成结果里经常是：同参数父级 -> Direct 叠加树 -> 同参数 Simple1D 子树。
        // 因此需要穿透 Direct / 其它 BlendTree，把已知参数范围继续传给子 Motion。
        if (tree.Children.Count > 0)
        {
            bool changed = false;
            var newChildren = ImmutableList<VirtualBlendTree.VirtualChildMotion>.Empty;

            foreach (var child in tree.Children)
            {
                var simplifiedChildMotion = SimplifyMotionForKnownParameterRange(child.Motion, parameterName, knownMin, knownMax);
                if (!ReferenceEquals(simplifiedChildMotion, child.Motion))
                    changed = true;
                var newChild = CloneChildMotion(child, simplifiedChildMotion);
                newChildren = newChildren.Add(newChild);
            }

            if (changed)
            {
                return CloneBlendTreeWithChildren(tree, newChildren);
            }
        }

        return motion;
    }

    VirtualBlendTree.VirtualChildMotion CloneChildMotion(
        VirtualBlendTree.VirtualChildMotion source,
        VirtualMotion motion)
    {
        return new VirtualBlendTree.VirtualChildMotion
        {
            Motion = motion,
            CycleOffset = source.CycleOffset,
            DirectBlendParameter = source.DirectBlendParameter,
            Mirror = source.Mirror,
            Threshold = source.Threshold,
            Position = source.Position,
            TimeScale = source.TimeScale
        };
    }

    VirtualBlendTree CloneBlendTreeWithChildren(
        VirtualBlendTree source,
        ImmutableList<VirtualBlendTree.VirtualChildMotion> children)
    {
        var clone = VirtualBlendTree.Create(source.Name);
        clone.BlendType = source.BlendType;
        clone.BlendParameter = source.BlendParameter;
        clone.BlendParameterY = source.BlendParameterY;
        clone.MinThreshold = source.MinThreshold;
        clone.MaxThreshold = source.MaxThreshold;
        clone.UseAutomaticThresholds = source.UseAutomaticThresholds;
        CopyOptionalNormalizedBlendValues(source, clone);
        clone.Children = children;
        return clone;
    }

    void CopyOptionalNormalizedBlendValues(VirtualBlendTree source, VirtualBlendTree target)
    {
        if (source == null || target == null)
            return;

        var property = typeof(VirtualBlendTree).GetProperty(
            "NormalizedBlendValues",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic);

        if (property == null || !property.CanRead || !property.CanWrite)
            return;

        property.SetValue(target, property.GetValue(source, null), null);
    }

    void GetChildIntegerDomain(
        string parameterName,
        List<VirtualBlendTree.VirtualChildMotion> children,
        int index,
        out int? min,
        out int? max)
    {
        int threshold = RoundToNonNegativeInt(children[index].Threshold);

        if (children.Count == 1)
        {
            min = null;
            max = null;
            return;
        }

        if (index == 0)
        {
            min = null;
            max = threshold;
            return;
        }

        if (index == children.Count - 1)
        {
            min = threshold;
            max = GetKnownDiscreteParameterMax(parameterName);
            return;
        }

        min = threshold;
        max = threshold;
    }

    bool RangesIntersect(int? aMin, int? aMax, int? bMin, int? bMax)
    {
        if (aMax.HasValue && bMin.HasValue && aMax.Value < bMin.Value)
            return false;

        if (bMax.HasValue && aMin.HasValue && bMax.Value < aMin.Value)
            return false;

        return true;
    }

    List<RoundedSample> BuildRoundedIntegerSamples(string parameterName, float lo, float hi, bool addSafetyGuardForOpenRange = false)
    {
        var sparseSamples = BuildSparseRoundedIntegerSamples(parameterName, lo, hi, addSafetyGuardForOpenRange);

        // Compact Mode 关闭时，嵌套混合树也要保留原始“完整整数阈值表”的行为。
        // 例如单个状态命中 100 时，紧凑模式只生成 99/100/101；关闭紧凑模式后也从首个保护阈值开始，
        // 不再额外生成 0..98 这种被 99 Empty 覆盖的冗余阈值。多个有效区间之间仍会补齐整数 Empty。
        if (_settings.compactMode || HasNonIntegerThreshold(sparseSamples))
            return sparseSamples;

        return ExpandRoundedSamplesToDense(sparseSamples);
    }

    bool HasNonIntegerThreshold(List<RoundedSample> samples)
    {
        return samples != null && samples.Any(s => !IsNearlyInteger(s.Threshold));
    }

    bool IsNearlyInteger(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
            return false;

        return Mathf.Abs(value - Mathf.Round(value)) <= 0.0001f;
    }

    List<RoundedSample> BuildSparseRoundedIntegerSamples(string parameterName, float lo, float hi, bool addSafetyGuardForOpenRange = false)
    {
        bool hasLower = float.IsFinite(lo);
        bool hasUpper = float.IsFinite(hi);
        var samples = new List<RoundedSample>();
        int? knownParameterMax = GetKnownDiscreteParameterMax(parameterName);

        if (!hasLower && !hasUpper)
        {
            samples.Add(MakeSample(0, RoundedSampleKind.Active, null, null));
            return samples;
        }

        int firstActive;
        int lastActive;

        if (hasLower && hasUpper)
        {
            // 有上下界：这是“等于某个整数值”或有限区间。
            // 例如 0.995 < 参数 < 1.005 => 0 Empty, 1 Active, 2 Empty。
            float centerValue = (lo + hi) / 2f;
            if (!IsNearlyInteger(centerValue))
            {
                // MA MenuItem 可以手动设置小数 value；这时条件会是 value ± 0.005。
                // 不能把它四舍五入成整数采样，否则 0.5 这类值会落到错误阈值。
                float lowerFloatGuard = Math.Max(0f, lo);
                float upperFloatGuard = Math.Max(hi, centerValue);

                if (lowerFloatGuard < centerValue)
                    samples.Add(MakeSample(lowerFloatGuard, RoundedSampleKind.LowerInactive, null, null));

                samples.Add(MakeSample(Math.Max(0f, centerValue), RoundedSampleKind.Active, null, null));

                if (upperFloatGuard > centerValue)
                    samples.Add(MakeSample(upperFloatGuard, RoundedSampleKind.UpperInactive, null, null));

                return samples
                    .GroupBy(s => s.Threshold)
                    .Select(g => g.Last())
                    .OrderBy(s => s.Threshold)
                    .ToList();
            }

            firstActive = RoundToNonNegativeInt(lo + 0.5f);
            lastActive = RoundToNonNegativeInt(hi - 0.5f);
            if (lastActive < firstActive)
            {
                int center = RoundToNonNegativeInt(centerValue);
                firstActive = center;
                lastActive = center;
            }

            int lowerGuard = Math.Max(0, firstActive - 1);
            int upperGuard = Math.Max(lastActive + 1, firstActive + 1);

            if (lowerGuard < firstActive)
                samples.Add(MakeSample(lowerGuard, RoundedSampleKind.LowerInactive, null, lowerGuard));

            int activeUpper = knownParameterMax.HasValue
                ? Math.Min(lastActive, knownParameterMax.Value)
                : lastActive;

            for (int v = firstActive; v <= activeUpper; v++)
                samples.Add(MakeSample(v, RoundedSampleKind.Active, v, v));

            if (!knownParameterMax.HasValue || upperGuard <= knownParameterMax.Value)
                samples.Add(MakeSample(upperGuard, RoundedSampleKind.UpperInactive, upperGuard, knownParameterMax));
        }
        else if (hasLower)
        {
            // 只有下界：这是 “参数 > x” / Bool True。
            // 主状态参数保持原始开区间语义：例如 >0.5 => 0 Empty, 1 Active。
            // 对未知上限的连续参数，为了避免相邻 1D 节点插值串色，
            // 调用方可以要求额外添加一个上界 Empty 安全隔离点。
            firstActive = RoundToNonNegativeInt(lo + 0.5f);
            int lowerGuard = Math.Max(0, firstActive - 1);

            if (lowerGuard < firstActive)
                samples.Add(MakeSample(lowerGuard, RoundedSampleKind.LowerInactive, null, lowerGuard));

            if (knownParameterMax.HasValue)
            {
                // __MA/AutoParam 与 __MA/ActiveSelfProxy 这类参数的离散取值上限是已知的。
                // 对这种参数不能额外添加 firstActive+1 这种超出预设范围的安全 Empty，
                // 也不能把 “>0.5” 错误截断成只在 1 有效。
                if (firstActive <= knownParameterMax.Value)
                    samples.Add(MakeSample(firstActive, RoundedSampleKind.Active, firstActive, knownParameterMax));
            }
            else if (addSafetyGuardForOpenRange)
            {
                samples.Add(MakeSample(firstActive, RoundedSampleKind.Active, firstActive, firstActive));
                samples.Add(MakeSample(Math.Max(firstActive + 1, 1), RoundedSampleKind.UpperInactive, firstActive + 1, null, true));
            }
            else
            {
                samples.Add(MakeSample(firstActive, RoundedSampleKind.Active, firstActive, null));
            }
        }
        else
        {
            // 只有上界：这是 “参数 < x” / Bool False。
            // 例如 Param < 0.5 => 0 Active, 1 Empty。
            // 不需要下界 Empty，因为 1D BlendTree 小于第一个阈值时会使用第一个 child。
            lastActive = RoundToNonNegativeInt(hi - 0.5f);
            int upperGuard = Math.Max(lastActive + 1, 1);

            samples.Add(MakeSample(lastActive, RoundedSampleKind.Active, null, lastActive));
            if (!knownParameterMax.HasValue || upperGuard <= knownParameterMax.Value)
                samples.Add(MakeSample(upperGuard, RoundedSampleKind.UpperInactive, upperGuard, knownParameterMax));
        }

        return samples
            .GroupBy(s => s.Threshold)
            .Select(g => g.Last())
            .OrderBy(s => s.Threshold)
            .ToList();
    }

    List<RoundedSample> ExpandRoundedSamplesToDense(List<RoundedSample> sparseSamples)
    {
        if (sparseSamples == null || sparseSamples.Count == 0)
            return new List<RoundedSample>();

        // Dense 只补齐“第一个保护阈值”到“最后一个保护阈值”之间的整数。
        // 例如只有 50 有动画时，稀疏阈值是 49/50/51；Dense 也只需要 49/50/51，
        // 因为参数小于 49 时会自动使用第一个 49 Empty，不需要额外放一个 0 Empty。
        int minThreshold = sparseSamples.Min(s => RoundToNonNegativeInt(s.Threshold));
        int maxThreshold = sparseSamples.Max(s => RoundToNonNegativeInt(s.Threshold));
        if (maxThreshold < minThreshold)
            maxThreshold = minThreshold;

        var activeThresholds = new HashSet<int>(sparseSamples
            .Where(s => s.Kind == RoundedSampleKind.Active)
            .Select(s => RoundToNonNegativeInt(s.Threshold)));
        var safetyThresholds = new HashSet<int>(sparseSamples
            .Where(s => s.IsSafetyGuard)
            .Select(s => RoundToNonNegativeInt(s.Threshold)));

        int? firstActive = activeThresholds.Count == 0 ? (int?)null : activeThresholds.Min();

        return Enumerable.Range(minThreshold, maxThreshold - minThreshold + 1)
            .Select(v =>
            {
                RoundedSampleKind kind;
                if (activeThresholds.Contains(v))
                {
                    kind = RoundedSampleKind.Active;
                }
                else if (firstActive.HasValue && v < firstActive.Value)
                {
                    kind = RoundedSampleKind.LowerInactive;
                }
                else
                {
                    kind = RoundedSampleKind.UpperInactive;
                }

                bool isFirst = v == minThreshold;
                bool isLast = v == maxThreshold;
                int? domainMin = isFirst ? (int?)null : v;
                int? domainMax = isLast ? (int?)null : v;

                return MakeSample(v, kind, domainMin, domainMax, safetyThresholds.Contains(v));
            })
            .ToList();
    }

    int RoundToNonNegativeInt(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
            return 0;
        return Math.Max(0, Mathf.RoundToInt(value));
    }

    VirtualBlendTree BuildParameterBlendTree(ParameterGroup group)
    {
        var paramTree = VirtualBlendTree.Create(SanitizeName(group.ParameterName));
        paramTree.BlendType = BlendTreeType.Simple1D;
        paramTree.BlendParameter = group.ParameterName;
        paramTree.UseAutomaticThresholds = false;

        foreach (float threshold in group.Thresholds)
        {
            var motion = BuildMotionForThreshold(group, threshold);

            paramTree.Children = paramTree.Children.Add(
                new VirtualBlendTree.VirtualChildMotion
                {
                    Motion = motion,
                    Threshold = threshold
                });
        }

        return paramTree;
    }

    VirtualMotion BuildMotionForThreshold(ParameterGroup group, float threshold)
    {
        if (group != null)
            return BuildMotionForThresholdOverlay(group, threshold);

        var unconditionalClip = VirtualClip.Create($"{SanitizeName(group.ParameterName)}_t{threshold}");
        bool hasUnconditionalCurves = false;
        var nestedMotions = new List<VirtualMotion>();

        foreach (var layer in group.Layers)
        {
            var layerMotion = BuildLayerMotionForThreshold(
                layer,
                threshold,
                $"{SanitizeName(group.ParameterName)}_t{threshold}_l{layer.OriginalIndex}");

            if (layerMotion == null || layerMotion.Motion == null) continue;

            if (layerMotion.CanMergeIntoClip)
            {
                if (MergeMotionIntoClip(unconditionalClip, layerMotion.Motion))
                {
                    hasUnconditionalCurves = true;
                }
            }
            else
            {
                nestedMotions.Add(layerMotion.Motion);
            }
        }

        var baseMotion = hasUnconditionalCurves ? (VirtualMotion)unconditionalClip : GetSharedEmptyClip();
        nestedMotions = MergeMatchingStructuredMotions(
            nestedMotions,
            $"{SanitizeName(group.ParameterName)}_t{threshold}_Nested");

        if (nestedMotions.Count == 0)
            return baseMotion;

        // 如果当前阈值下没有可合并的无条件曲线，并且只有一个带条件的嵌套 Motion，
        // 直接返回这个嵌套 Motion。否则会额外生成一个只包含 Empty + 单个子树的 Direct BlendTree，
        // 例如“颜色_t0_Direct -> Empty + ActiveSelfProxy 条件树”，这是冗余节点。
        // 只有存在无条件曲线，或同一个阈值下有多个嵌套 Motion 需要叠加时，才需要 Direct 树。
        if (!hasUnconditionalCurves && nestedMotions.Count == 1)
            return nestedMotions[0];

        var directTree = VirtualBlendTree.Create($"{SanitizeName(group.ParameterName)}_t{threshold}_Direct");
        directTree.BlendType = BlendTreeType.Direct;
        directTree.BlendParameter = ROOT_PARAM;
        directTree.BlendParameterY = ROOT_PARAM;
        directTree.Children = directTree.Children.Add(
            new VirtualBlendTree.VirtualChildMotion
            {
                Motion = baseMotion,
                DirectBlendParameter = ROOT_PARAM
            });

        foreach (var motion in nestedMotions)
        {
            directTree.Children = directTree.Children.Add(
                new VirtualBlendTree.VirtualChildMotion
                {
                    Motion = motion,
                    DirectBlendParameter = ROOT_PARAM
                });
        }

        return directTree;
    }

    VirtualMotion BuildMotionForThresholdOverlay(ParameterGroup group, float threshold)
    {
        VirtualMotion current = GetSharedEmptyClip();
        bool hasAnyMotion = false;
        string namePrefix = $"{SanitizeName(group.ParameterName)}_t{threshold}";

        foreach (var layer in group.Layers)
        {
            var layerMotion = BuildLayerMotionForThreshold(
                layer,
                threshold,
                $"{namePrefix}_l{layer.OriginalIndex}");

            if (layerMotion == null || layerMotion.Motion == null) continue;

            current = hasAnyMotion
                ? OverlayLayerMotions(current, layerMotion.Motion, $"{namePrefix}_o{layer.OriginalIndex}")
                : layerMotion.Motion;
            hasAnyMotion = true;
        }

        return hasAnyMotion ? current : GetSharedEmptyClip();
    }

    VirtualMotion OverlayLayerMotions(VirtualMotion lower, VirtualMotion upper, string name)
    {
        lower = NormalizeEmptyMotion(lower);
        upper = NormalizeEmptyMotion(upper);

        bool lowerEmpty = !MotionHasAnyCurves(lower);
        bool upperEmpty = !MotionHasAnyCurves(upper);
        if (upperEmpty) return lower;
        if (lowerEmpty) return upper;

        if (lower is VirtualClip && upper is VirtualClip)
            return MergeMotionsIntoSingleMotion(new[] { lower, upper }, name);

        if (lower is VirtualBlendTree lowerTree && upper is VirtualClip)
            return OverlayClipOntoTree(lowerTree, upper, name);

        if (lower is VirtualClip && upper is VirtualBlendTree upperTree)
            return OverlayTreeOntoMotion(lower, upperTree, name);

        if (lower is VirtualBlendTree lowerBlendTree && upper is VirtualBlendTree upperBlendTree)
        {
            string lowerSignature = GetMotionStructureSignature(lowerBlendTree);
            string upperSignature = GetMotionStructureSignature(upperBlendTree);
            if (!string.IsNullOrEmpty(lowerSignature) && lowerSignature == upperSignature)
                return OverlayMatchingTrees(lowerBlendTree, upperBlendTree, name);
        }

        return BuildDirectOverlayMotion(lower, upper, name);
    }

    VirtualMotion OverlayClipOntoTree(VirtualBlendTree lowerTree, VirtualMotion upperClip, string name)
    {
        var children = ImmutableList<VirtualBlendTree.VirtualChildMotion>.Empty;
        for (int i = 0; i < lowerTree.Children.Count; i++)
        {
            var child = lowerTree.Children[i];
            var mergedChild = OverlayLayerMotions(child.Motion, upperClip, $"{name}_c{i}");
            children = children.Add(CloneChildMotion(child, mergedChild));
        }

        var clone = CloneBlendTreeWithChildren(lowerTree, children);
        clone.Name = name;
        return clone;
    }

    VirtualMotion OverlayTreeOntoMotion(VirtualMotion lower, VirtualBlendTree upperTree, string name)
    {
        var children = ImmutableList<VirtualBlendTree.VirtualChildMotion>.Empty;
        for (int i = 0; i < upperTree.Children.Count; i++)
        {
            var child = upperTree.Children[i];
            var mergedChild = OverlayLayerMotions(lower, child.Motion, $"{name}_c{i}");
            children = children.Add(CloneChildMotion(child, mergedChild));
        }

        var clone = CloneBlendTreeWithChildren(upperTree, children);
        clone.Name = name;
        return clone;
    }

    VirtualMotion OverlayMatchingTrees(VirtualBlendTree lowerTree, VirtualBlendTree upperTree, string name)
    {
        var children = ImmutableList<VirtualBlendTree.VirtualChildMotion>.Empty;
        for (int i = 0; i < upperTree.Children.Count; i++)
        {
            var upperChild = upperTree.Children[i];
            var lowerChild = lowerTree.Children[i];
            var mergedChild = OverlayLayerMotions(lowerChild.Motion, upperChild.Motion, $"{name}_c{i}");
            children = children.Add(CloneChildMotion(upperChild, mergedChild));
        }

        var clone = CloneBlendTreeWithChildren(upperTree, children);
        clone.Name = name;
        return clone;
    }

    VirtualMotion BuildDirectOverlayMotion(VirtualMotion lower, VirtualMotion upper, string name)
    {
        var directTree = VirtualBlendTree.Create(name);
        directTree.BlendType = BlendTreeType.Direct;
        directTree.BlendParameter = ROOT_PARAM;
        directTree.BlendParameterY = ROOT_PARAM;
        directTree.Children = directTree.Children.Add(
            new VirtualBlendTree.VirtualChildMotion
            {
                Motion = lower,
                DirectBlendParameter = ROOT_PARAM
            });
        directTree.Children = directTree.Children.Add(
            new VirtualBlendTree.VirtualChildMotion
            {
                Motion = upper,
                DirectBlendParameter = ROOT_PARAM
            });
        return directTree;
    }

    List<VirtualMotion> MergeMatchingStructuredMotions(List<VirtualMotion> motions, string namePrefix)
    {
        if (motions == null || motions.Count <= 1)
            return motions ?? new List<VirtualMotion>();

        var groups = new List<(string Signature, List<VirtualMotion> Motions)>();
        var unmergeable = new List<VirtualMotion>();

        foreach (var motion in motions)
        {
            string signature = GetMotionStructureSignature(motion);
            if (string.IsNullOrEmpty(signature))
            {
                unmergeable.Add(motion);
                continue;
            }

            int groupIndex = groups.FindIndex(g => g.Signature == signature);
            if (groupIndex < 0)
            {
                groups.Add((signature, new List<VirtualMotion> { motion }));
            }
            else
            {
                groups[groupIndex].Motions.Add(motion);
            }
        }

        var result = new List<VirtualMotion>();
        int mergedIndex = 0;
        foreach (var group in groups)
        {
            if (group.Motions.Count == 1)
            {
                result.Add(group.Motions[0]);
                continue;
            }

            var merged = MergeMotionsWithSameStructure(group.Motions, $"{namePrefix}_{mergedIndex++}");
            if (merged != null)
                result.Add(merged);
            else
                result.AddRange(group.Motions);
        }

        result.AddRange(unmergeable);
        return result;
    }

    VirtualMotion MergeMotionsWithSameStructure(List<VirtualMotion> motions, string name)
    {
        if (motions == null || motions.Count == 0)
            return GetSharedEmptyClip();

        if (motions.All(m => m is VirtualClip))
            return MergeMotionsIntoSingleMotion(motions, name);

        if (!motions.All(m => m is VirtualBlendTree))
            return null;

        var trees = motions.Cast<VirtualBlendTree>().ToList();
        var prototype = trees[0];
        if (trees.Any(t => GetMotionStructureSignature(t) != GetMotionStructureSignature(prototype)))
            return null;

        var children = ImmutableList<VirtualBlendTree.VirtualChildMotion>.Empty;
        for (int i = 0; i < prototype.Children.Count; i++)
        {
            var childMotions = trees.Select(t => t.Children[i].Motion).ToList();
            var mergedChild = MergeMotionsWithSameStructure(childMotions, $"{name}_c{i}");
            if (mergedChild == null)
                return null;

            children = children.Add(CloneChildMotion(prototype.Children[i], mergedChild));
        }

        var clone = CloneBlendTreeWithChildren(prototype, children);
        clone.Name = name;
        return clone;
    }

    string GetMotionStructureSignature(VirtualMotion motion)
    {
        if (motion is VirtualClip)
            return "clip";

        if (motion is not VirtualBlendTree tree)
            return null;

        var childSignatures = tree.Children
            .Select(child =>
            {
                string childSignature = GetMotionStructureSignature(child.Motion);
                if (string.IsNullOrEmpty(childSignature))
                    return null;

                return $"{child.Threshold:R}:{child.DirectBlendParameter}:{child.Position}:{childSignature}";
            })
            .ToList();

        if (childSignatures.Any(string.IsNullOrEmpty))
            return null;

        return $"bt:{tree.BlendType}:{tree.BlendParameter}:{tree.BlendParameterY}:{tree.UseAutomaticThresholds}:{string.Join("|", childSignatures)}";
    }

    LayerThresholdMotion BuildLayerMotionForThreshold(
        AnalyzedLayer layer,
        float threshold,
        string name)
    {
        var defaultState = layer.States.FirstOrDefault(s => s.IsDefault);
        if (defaultState == null)
            return null;

        var activeCandidates = layer.States
            .Where(s => !s.IsDefault && IsStateActiveAtThreshold(layer, s, threshold))
            .OrderBy(s => s.Order)
            .ToList();

        if (activeCandidates.Count == 0)
        {
            return new LayerThresholdMotion
            {
                Motion = NormalizeEmptyMotion(defaultState.Motion),
                CanMergeIntoClip = true,
            };
        }

        VirtualMotion current = NormalizeEmptyMotion(defaultState.Motion);
        bool requiresNestedTree = false;

        // 后面的低优先级状态先作为 fallback 包进去；前面的高优先级 Entry 状态在外层。
        for (int i = activeCandidates.Count - 1; i >= 0; i--)
        {
            var state = activeCandidates[i];
            var activeMotion = NormalizeEmptyMotion(state.Motion);

            if (state.SecondaryConditions == null || state.SecondaryConditions.Count == 0)
            {
                current = activeMotion;
            }
            else
            {
                current = BuildSecondaryConditionTree(
                    activeMotion,
                    current ?? GetSharedEmptyClip(),
                    state.SecondaryConditions,
                    $"{name}_state{state.Order}");

                requiresNestedTree = true;
            }
        }

        return new LayerThresholdMotion
        {
            Motion = current ?? GetSharedEmptyClip(),
            CanMergeIntoClip = !requiresNestedTree
        };
    }

    bool IsStateActiveAtThreshold(AnalyzedLayer layer, StateInfo state, float threshold)
    {
        bool inRange = IsThresholdInRange(threshold, state.ThresholdLo, state.ThresholdHi);
        return state.IsInverted ? !inRange : inRange;
    }

    VirtualMotion BuildSecondaryConditionTree(
        VirtualMotion activeMotion,
        VirtualMotion inactiveMotion,
        List<SecondaryCondition> conditions,
        string name)
    {
        VirtualMotion current = activeMotion;

        for (int i = conditions.Count - 1; i >= 0; i--)
        {
            var condition = conditions[i];
            float lo = condition.ActiveWhenGreater ? condition.Threshold : float.NegativeInfinity;
            float hi = condition.ActiveWhenGreater ? float.PositiveInfinity : condition.Threshold;

            current = BuildRoundedIntegerConditionTree(
                condition.ParameterName,
                lo,
                hi,
                false,
                current,
                inactiveMotion,
                inactiveMotion,
                $"{name}_{SanitizeName(condition.ParameterName)}",
                true);
        }

        return current;
    }

    VirtualMotion BuildConditionRangeTree(
        VirtualMotion activeMotion,
        VirtualMotion inactiveMotion,
        List<ConditionRange> conditions,
        string name)
    {
        VirtualMotion current = activeMotion;
        var fallbackMotion = inactiveMotion ?? GetSharedEmptyClip();

        for (int i = conditions.Count - 1; i >= 0; i--)
        {
            var condition = conditions[i];
            if (condition == null || string.IsNullOrEmpty(condition.ParameterName))
                continue;

            current = BuildRoundedIntegerConditionTree(
                condition.ParameterName,
                condition.ThresholdLo,
                condition.ThresholdHi,
                condition.IsInverted,
                current,
                fallbackMotion,
                fallbackMotion,
                $"{name}_{SanitizeName(condition.ParameterName)}",
                condition.AddSafetyGuardForOpenRange);
        }

        return current;
    }

    VirtualMotion MergeMotionsIntoSingleMotion(IEnumerable<VirtualMotion> motions, string clipName)
    {
        var motionList = motions
            .Where(m => m != null)
            .ToList();

        if (motionList.Count == 0)
            return GetSharedEmptyClip();

        var mergedClip = VirtualClip.Create(clipName);
        bool hasCurves = false;

        foreach (var motion in motionList)
        {
            if (MergeMotionIntoClip(mergedClip, motion))
                hasCurves = true;
        }

        // 没有任何曲线时统一回退到共享 Empty，避免生成或保留一堆不同名字的空 Motion。
        return hasCurves ? (VirtualMotion)mergedClip : GetSharedEmptyClip();
    }

    VirtualMotion NormalizeEmptyMotion(VirtualMotion motion)
    {
        if (motion == null)
            return GetSharedEmptyClip();

        return MotionHasAnyCurves(motion) ? motion : GetSharedEmptyClip();
    }

    bool MotionHasAnyCurves(VirtualMotion motion)
    {
        if (motion == null)
            return false;

        if (motion is VirtualClip clip)
        {
            return clip.GetFloatCurveBindings().Any()
                || clip.GetObjectCurveBindings().Any();
        }

        if (motion is VirtualBlendTree bt)
            return bt.Children.Any(child => MotionHasAnyCurves(child.Motion));

        // 未知 Motion 类型保守保留，避免误判为空。
        return true;
    }

    VirtualClip GetSharedEmptyClip()
    {
        if (_sharedEmptyClip == null)
            _sharedEmptyClip = VirtualClip.Create("Empty");
        return _sharedEmptyClip;
    }

    bool IsThresholdInRange(float threshold, float lo, float hi)
    {
        bool aboveLo = float.IsNegativeInfinity(lo) || threshold > lo;
        bool belowHi = float.IsPositiveInfinity(hi) || threshold < hi;
        return aboveLo && belowHi;
    }

    bool MergeMotionIntoClip(VirtualClip target, VirtualMotion motion)
    {
        if (motion is VirtualClip sourceClip)
        {
            return MergeClipCurves(target, sourceClip);
        }
        else if (motion is VirtualBlendTree bt)
        {
            bool any = false;
            foreach (var child in bt.Children)
            {
                if (child.Motion is VirtualClip childClip)
                    any |= MergeClipCurves(target, childClip);
            }
            return any;
        }
        return false;
    }

    bool MergeClipCurves(VirtualClip target, VirtualClip source)
    {
        bool any = false;

        // Float
        foreach (var binding in source.GetFloatCurveBindings())
        {
            var curve = source.GetFloatCurve(binding);
            if (curve != null)
            {
                target.SetFloatCurve(binding, curve);
                any = true;
            }
        }

        // Object reference
        foreach (var binding in source.GetObjectCurveBindings())
        {
            var objCurve = source.GetObjectCurve(binding);
            if (objCurve != null)
            {
                target.SetObjectCurve(binding, objCurve);
                any = true;
            }
        }

        return any;
    }

    #endregion

    #region 注入

    void InjectBlendTreeLayer(VirtualBlendTree rootBlendTree)
    {
        VirtualLayer existingLayer = null;
        foreach (var l in _fx.Layers)
        {
            if (l.Name == BLEND_TREE_LAYER_NAME)
            {
                existingLayer = l;
                break;
            }
        }

        VirtualLayer layer;
        if (existingLayer != null)
        {
            layer = existingLayer;
        }
        else
        {
            layer = _fx.AddLayer(new LayerPriority(0), BLEND_TREE_LAYER_NAME);
        }

        layer.DefaultWeight = 1f;
        layer.BlendingMode = AnimatorLayerBlendingMode.Override;

        var sm = layer.StateMachine;
        sm.States = ImmutableList<VirtualStateMachine.VirtualChildState>.Empty;
        sm.DefaultState = null;
        sm.StateMachines = ImmutableList<VirtualStateMachine.VirtualChildStateMachine>.Empty;
        sm.AnyStateTransitions = ImmutableList<VirtualStateTransition>.Empty;
        sm.EntryTransitions = ImmutableList<VirtualTransition>.Empty;

        var rootState = sm.AddState("RootBlendTree", rootBlendTree);
        rootState.WriteDefaultValues = true;
        sm.DefaultState = rootState;
    }

    #endregion

    #region 工具

    string GetNestedLayerSignature(AnalyzedLayer layer)
    {
        return string.Join("||", layer.States
            .Where(s => !s.IsDefault)
            .OrderBy(s => s.Order)
            .Select(GetStateConditionSignature));
    }

    string GetStateConditionSignature(StateInfo state)
    {
        string activeSamples = string.Join(",", BuildRoundedIntegerSamples(state.ParameterName, state.ThresholdLo, state.ThresholdHi)
            .Where(s => s.Kind == RoundedSampleKind.Active)
            .Select(s => s.Threshold));

        string secondary = "";
        if (state.SecondaryConditions != null && state.SecondaryConditions.Count > 0)
        {
            secondary = string.Join(",", state.SecondaryConditions.Select(GetSecondaryConditionSignature));
        }

        return $"param={state.ParameterName};inv={state.IsInverted};active=[{activeSamples}];secondary=[{secondary}]";
    }

    string GetSecondaryConditionSignature(SecondaryCondition condition)
    {
        float lo = condition.ActiveWhenGreater ? condition.Threshold : float.NegativeInfinity;
        float hi = condition.ActiveWhenGreater ? float.PositiveInfinity : condition.Threshold;
        string activeSamples = string.Join(",", BuildRoundedIntegerSamples(condition.ParameterName, lo, hi)
            .Where(s => s.Kind == RoundedSampleKind.Active)
            .Select(s => s.Threshold));

        return $"param={condition.ParameterName};greater={condition.ActiveWhenGreater};active=[{activeSamples}]";
    }

    void CacheAutoParamMaxValues(List<AnalyzedLayer> layers)
    {
        _autoParamMaxValues.Clear();
        if (layers == null) return;

        foreach (var layer in layers)
        {
            foreach (var state in layer.States.Where(s => !s.IsDefault))
            {
                AddAutoParamObservedMax(state.ParameterName, state.ThresholdLo, state.ThresholdHi);

                if (state.SecondaryConditions == null) continue;
                foreach (var condition in state.SecondaryConditions)
                {
                    float lo = condition.ActiveWhenGreater ? condition.Threshold : float.NegativeInfinity;
                    float hi = condition.ActiveWhenGreater ? float.PositiveInfinity : condition.Threshold;
                    AddAutoParamObservedMax(condition.ParameterName, lo, hi);
                }
            }
        }
    }

    void AddAutoParamObservedMax(string parameterName, float lo, float hi)
    {
        if (!IsAutoParam(parameterName)) return;

        int? observedMax = GetObservedActiveMax(lo, hi);
        if (!observedMax.HasValue) return;

        if (!_autoParamMaxValues.TryGetValue(parameterName, out int currentMax) || observedMax.Value > currentMax)
            _autoParamMaxValues[parameterName] = observedMax.Value;
    }

    int? GetObservedActiveMax(float lo, float hi)
    {
        bool hasLower = float.IsFinite(lo);
        bool hasUpper = float.IsFinite(hi);

        if (!hasLower && !hasUpper)
            return null;

        if (hasLower && hasUpper)
        {
            int firstActive = RoundToNonNegativeInt(lo + 0.5f);
            int lastActive = RoundToNonNegativeInt(hi - 0.5f);
            if (lastActive < firstActive)
                return RoundToNonNegativeInt((lo + hi) / 2f);
            return lastActive;
        }

        if (hasLower)
            return RoundToNonNegativeInt(lo + 0.5f);

        return RoundToNonNegativeInt(hi - 0.5f);
    }

    bool IsAutoParam(string parameterName)
    {
        if (string.IsNullOrEmpty(parameterName)) return false;
        return parameterName.StartsWith("MA/AutoParam", StringComparison.Ordinal)
            || parameterName.StartsWith("_MA/AutoParam", StringComparison.Ordinal)
            || parameterName.StartsWith("__MA/AutoParam", StringComparison.Ordinal);
    }

    bool IsActiveSelfProxyParameter(string parameterName)
    {
        if (string.IsNullOrEmpty(parameterName)) return false;
        return parameterName.StartsWith("__ActiveSelfProxy", StringComparison.Ordinal)
            || parameterName.StartsWith("MA/ActiveSelfProxy", StringComparison.Ordinal)
            || parameterName.StartsWith("_MA/ActiveSelfProxy", StringComparison.Ordinal)
            || parameterName.StartsWith("__MA/ActiveSelfProxy", StringComparison.Ordinal);
    }

    int? GetKnownDiscreteParameterMax(string parameterName)
    {
        if (IsActiveSelfProxyParameter(parameterName))
            return 1;

        if (IsAutoParam(parameterName))
            return _autoParamMaxValues.TryGetValue(parameterName, out int maxValue) ? (int?)maxValue : null;

        return null;
    }

    bool StateHasBehaviours(VirtualState state, out string behaviourSummary)
    {
        behaviourSummary = null;
        if (state == null || state.Behaviours == null || state.Behaviours.Count == 0)
            return false;

        var behaviourNames = state.Behaviours
            .Where(b => b != null)
            .Select(b => b.GetType().Name)
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        int missingCount = state.Behaviours.Count(b => b == null);
        if (missingCount > 0)
            behaviourNames.Add($"Missing/Null x{missingCount}");

        behaviourSummary = behaviourNames.Count > 0
            ? string.Join(", ", behaviourNames)
            : $"{state.Behaviours.Count} 个 StateMachineBehaviour";

        return true;
    }

    IEnumerable<string> GetExcludedParameterPrefixes()
    {
        if (_settings == null)
            return Enumerable.Empty<string>();

        if (!TryReadMember(_settings, out object rawPrefixes,
                "excludedParameterPrefixes",
                "excludedParamPrefixes",
                "parameterExcludedPrefixes",
                "excludedParameterNamePrefixes"))
            return Enumerable.Empty<string>();

        if (rawPrefixes == null)
            return Enumerable.Empty<string>();

        if (rawPrefixes is string singlePrefix)
            return new[] { singlePrefix };

        if (rawPrefixes is IEnumerable<string> stringPrefixes)
            return stringPrefixes;

        if (rawPrefixes is System.Collections.IEnumerable enumerablePrefixes)
        {
            return enumerablePrefixes
                .Cast<object>()
                .Select(item => item?.ToString())
                .Where(item => !string.IsNullOrEmpty(item));
        }

        return Enumerable.Empty<string>();
    }

    bool HasExcludedPrefix(string name, IEnumerable<string> prefixes, out string matchedPrefix)
    {
        matchedPrefix = null;
        if (string.IsNullOrEmpty(name) || prefixes == null)
            return false;

        foreach (var rawPrefix in prefixes)
        {
            var prefix = rawPrefix?.Trim();
            if (string.IsNullOrEmpty(prefix))
                continue;

            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                matchedPrefix = prefix;
                return true;
            }
        }

        return false;
    }

    string FormatPrefixList(IEnumerable<string> prefixes)
    {
        if (prefixes == null)
            return "无";

        var validPrefixes = prefixes
            .Select(p => p?.Trim())
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();

        return validPrefixes.Count == 0
            ? "无"
            : string.Join(", ", validPrefixes.Select(p => $"\"{p}\""));
    }

    string FormatRejectReason(string reason)
    {
        if (string.IsNullOrEmpty(reason)) return "";

        return reason
            .Replace("No state machine", "没有状态机")
            .Replace("Insufficient state count", "状态数量不足")
            .Replace("No default state", "没有默认状态")
            .Replace("No Entry Transition", "没有 Entry Transition")
            .Replace("Failed to extract parameter name", "无法提取参数名")
            .Replace("Multi-state layer", "多状态图层")
            .Replace("conditional states", "个条件状态")
            .Replace("enable Multi-State Layers", "请开启 Multi-State Layers")
            .Replace("has no corresponding Entry Transition", "没有对应的 Entry Transition")
            .Replace("Unsupported condition mode", "不支持的条件模式")
            .Replace("Invalid condition range", "无效条件范围")
            .Replace("Failed to identify main parameter", "无法识别主参数")
            .Replace("Inverted multi-parameter AND conditions are not supported", "不支持反向模式下的多参数 AND 条件")
            .Replace("State", "状态")
            .Replace("has outgoing transition to another state", "存在跳转到其它状态的 Transition")
            .Replace("Excluded by layer prefix", "命中图层名前缀排除项")
            .Replace("Excluded by state prefix", "命中状态名前缀排除项")
            .Replace("Excluded by parameter prefix", "命中参数名前缀排除项")
            .Replace("Manual simple layer rejected", "手动简单图层拒绝")
            .Replace("Manual simple layer pattern not matched", "未匹配手动简单图层结构")
            .Replace("requires exactly 2 states", "需要刚好 2 个状态")
            .Replace("transition is unsafe", "Transition 设置不安全")
            .Replace("state", "状态");
    }

    string DescribeLayerStrategy(AnalyzedLayer layer)
    {
        string mode = layer.RequiresNestedLayerTree ? "图层级嵌套" : "参数分组";
        string source = layer.IsExternalLayer ? "外部图层" : "MA 图层";
        string states = string.Join("；", layer.States
            .Where(s => !s.IsDefault)
            .OrderBy(s => s.Order)
            .Select(s => $"状态{s.Order}({s.StateName}): 参数={s.ParameterName}, 范围={DescribeThresholdRange(s.ThresholdLo, s.ThresholdHi)}" +
                (s.IsInverted ? ", 反向" : "") +
                (s.SecondaryConditions != null && s.SecondaryConditions.Count > 0
                    ? $", 二级条件={s.SecondaryConditions.Count}"
                    : "")));
        return $"来源={source}，模式={mode}，多状态={layer.HasMultipleConditionalStates}，参数=[{string.Join(", ", layer.MainParameterNames)}]，状态=[{states}]";
    }

    string DescribeThresholdRange(float lo, float hi)
    {
        bool hasLower = float.IsFinite(lo);
        bool hasUpper = float.IsFinite(hi);
        if (hasLower && hasUpper) return $"({lo}, {hi})";
        if (hasLower) return $">{lo}";
        if (hasUpper) return $"<{hi}";
        return "any";
    }

    void EnsureFloatParameter(string name, float defaultValue = 0f)
    {
        if (_fx.Parameters.TryGetValue(name, out var existing))
        {
            if (existing.type != AnimatorControllerParameterType.Float)
            {
                float preservedDefault = existing.type == AnimatorControllerParameterType.Int
                    ? existing.defaultInt
                    : existing.defaultBool ? 1f : 0f;
                var param = new AnimatorControllerParameter
                {
                    name = name,
                    type = AnimatorControllerParameterType.Float,
                    defaultFloat = preservedDefault
                };
                _fx.Parameters = _fx.Parameters.SetItem(name, param);
            }
            return;
        }

        var newParam = new AnimatorControllerParameter
        {
            name = name,
            type = AnimatorControllerParameterType.Float,
            defaultFloat = defaultValue
        };
        _fx.Parameters = _fx.Parameters.Add(name, newParam);
    }

    static string SanitizeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "Unnamed";
        var chars = name.Where(c => !char.IsControl(c) && c != '/' && c != '\\').ToArray();
        var result = new string(chars).Trim().Trim('.');
        return string.IsNullOrEmpty(result) ? "Unnamed" : result;
    }

    #endregion
}

}

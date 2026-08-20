using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Lumina.Excel.Sheets;
using OmenTools;
using OmenTools.Extensions;
using OmenTools.ImGuiOm;
using OmenTools.Interop.Game.Lumina;
using static OmenTools.Global.Globals;
using Camera = FFXIVClientStructs.FFXIV.Client.Game.Camera;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace DailyRoutines.ModulesPublic;

public unsafe class ModelSelectionFilter : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = "目标模型选择过滤",
        Description = "根据战斗状态及目标与自身的状态条件, 过滤左键点击模型或铭牌选中玩家或敌人",
        Category = ModuleCategory.System,
        Author = ["etnAtker"]
    };

    private Hook<InputManager.Delegates.GetInputStatus>? getInputStatusHook;
    private Hook<TargetSystem.Delegates.GetMouseOverObject>? getMouseOverObjectHook;

    private Config config = null!;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        getInputStatusHook = DService.Instance().Hook.HookFromMemberFunction
        (
            typeof(InputManager.MemberFunctionPointers),
            "GetInputStatus",
            (InputManager.Delegates.GetInputStatus)GetInputStatusDetour
        );
        getInputStatusHook.Enable();

        getMouseOverObjectHook = DService.Instance().Hook.HookFromMemberFunction
        (
            typeof(TargetSystem.MemberFunctionPointers),
            "GetMouseOverObject",
            (TargetSystem.Delegates.GetMouseOverObject)GetMouseOverObjectDetour
        );
        getMouseOverObjectHook.Enable();
    }

    protected override void ConfigUI()
    {
        DrawFilterConfig("玩家", config.Player, "Player");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawFilterConfig("敌人", config.Enemy, "Enemy");
    }

    private void DrawFilterConfig
    (
        string targetType,
        FilterConfig filter,
        string id
    )
    {
        using var scope = ImRaii.PushId(id);

        if (ImGui.Checkbox($"禁用通过点击模型或铭牌选中{targetType}", ref filter.Enabled))
            config.Save(this);

        using var indent = ImRaii.PushIndent();
        using var disabled = ImRaii.Disabled(!filter.Enabled);

        if (ImGui.Checkbox("仅在战斗中生效", ref filter.OnlyInCombat))
            config.Save(this);

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("状态规则");
        ImGui.SameLine();

        if (ImGuiOm.ButtonIcon("AddRule", FontAwesomeIcon.Plus, "添加规则"))
        {
            filter.Rules.Add(new());
            config.Save(this);
        }

        ImGuiOm.HelpMarker(" 多条状态规则之间为“或”，未添加规则时不限制状态。");

        if (filter.Rules.Count == 0)
        {
            ImGui.TextDisabled("未添加状态规则");
            return;
        }

        using var table = ImRaii.Table
        (
            "StatusRulesTable",
            3,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp
        );
        if (!table) return;

        ImGui.TableSetupColumn("目标具有状态", ImGuiTableColumnFlags.WidthStretch, 1);
        ImGui.TableSetupColumn("且自身具有状态", ImGuiTableColumnFlags.WidthStretch, 1);
        ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 50f * GlobalUIScale);
        ImGui.TableHeadersRow();

        var removeIndex = -1;

        for (var i = 0; i < filter.Rules.Count; i++)
        {
            var rule = filter.Rules[i];

            using var ruleScope = ImRaii.PushId(i);

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            DrawStatusInput("TargetStatus", ref rule.TargetStatus);

            ImGui.TableNextColumn();
            DrawStatusInput("SelfStatus", ref rule.SelfStatus);

            ImGui.TableNextColumn();
            if (ImGuiOm.ButtonIcon
                (
                    "删除规则",
                    FontAwesomeIcon.TrashAlt,
                    DailyRoutines.Manager.LanguageManager.Get("Delete"),
                    false
                ))
                removeIndex = i;
        }

        if (removeIndex < 0) return;

        filter.Rules.RemoveAt(removeIndex);
        config.Save(this);
    }

    private void DrawStatusInput
    (
        string id,
        ref string status
    )
    {
        ImGui.SetNextItemWidth(80f * GlobalUIScale);
        ImGui.InputTextWithHint($"##{id}", "ID", ref status, 10, ImGuiInputTextFlags.CharsDecimal);

        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);

        ImGui.SameLine();
        DrawStatusInfo(status);
    }

    private static void DrawStatusInfo
    (
        string status
    )
    {
        ImGui.AlignTextToFramePadding();

        if (string.IsNullOrWhiteSpace(status))
        {
            ImGui.TextDisabled("（任何）");
            return;
        }

        if (!uint.TryParse(status, out var id) ||
            !LuminaGetter.TryGetRow<Status>(id, out var statusRow) ||
            string.IsNullOrWhiteSpace(statusRow.Name.ToString()))
        {
            ImGui.TextColored(KnownColor.Red.ToVector4(), "（无效）");
            return;
        }

        using var group = ImRaii.Group();

        if (statusRow.Icon > 0 &&
            DService.Instance().Texture.TryGetFromGameIcon(new(statusRow.Icon), out var texture))
        {
            var w = texture.GetWrapOrEmpty().Width;
            var h = texture.GetWrapOrEmpty().Height;
            var targetH = ImGui.GetFrameHeight();
            ImGui.Image(texture.GetWrapOrEmpty().Handle, new(w * targetH / h, targetH));
            ImGui.SameLine(0, 4f * GlobalUIScale);
            ImGui.AlignTextToFramePadding();
        }

        ImGui.TextUnformatted($"{statusRow.Name.ToString()}");
    }

    private bool GetInputStatusDetour
    (
        InputManager* inputManager,
        InputCode inputCode
    )
    {
        var status = getInputStatusHook.Original(inputManager, inputCode);

        if (!status || inputCode != InputCode.MOUSE_OK) return status;

        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null) return status;

        var target = targetSystem->MouseOverNameplateTarget;
        if (target == null || target == targetSystem->GetTargetObject()) return status;

        return !ShouldFilter(target);
    }

    private GameObject* GetMouseOverObjectDetour
    (
        TargetSystem* system,
        int x,
        int y,
        GameObjectArray* objectArray,
        Camera* camera
    )
    {
        var target = getMouseOverObjectHook.Original(system, x, y, objectArray, camera);

        if (target == null ||
            target == system->GetTargetObject() ||
            !getInputStatusHook.Original(InputManager.Instance(), InputCode.MOUSE_OK))
            return target;

        if (ShouldFilter(target)) return null;

        return target;
    }

    private bool ShouldFilter
    (
        GameObject* target
    )
    {
        FilterConfig? filter = target->ObjectKind switch
        {
            ObjectKind.Pc => config.Player,
            ObjectKind.BattleNpc when target->IsCharacter() && ((Character*)target)->CharacterData.IsHostile
                => config.Enemy,
            _ => null
        };

        if (filter is not { Enabled: true }) return false;
        if (filter.OnlyInCombat && !DService.Instance().Condition[ConditionFlag.InCombat]) return false;
        if (filter.Rules.Count == 0) return true;

        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return false;

        var targetCharacter = (Character*)target;
        var selfCharacter = (Character*)localPlayer;

        return filter.Rules.Any
        (rule => HasStatusOrUnrestricted(targetCharacter, rule.TargetStatus) &&
                 HasStatusOrUnrestricted(selfCharacter, rule.SelfStatus)
        );
    }

    private static bool HasStatusOrUnrestricted
    (
        Character* character,
        string status
    )
    {
        if (string.IsNullOrWhiteSpace(status)) return true;

        return uint.TryParse(status, out var id) && character->HasStatus(id);
    }

    private class Config : ModuleConfig
    {
        public FilterConfig Enemy = new();
        public FilterConfig Player = new();
    }

    private class FilterConfig
    {
        public bool Enabled;
        public bool OnlyInCombat;
        public List<StatusRule> Rules = [];
    }

    private class StatusRule
    {
        public string SelfStatus = string.Empty;
        public string TargetStatus = string.Empty;
    }
}

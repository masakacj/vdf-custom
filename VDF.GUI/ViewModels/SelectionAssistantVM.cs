// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Collections.ObjectModel;
using System.Reactive;
using ReactiveUI;
using VDF.Core.Utils;
using VDF.GUI.Data;
using VDF.GUI.Views;

namespace VDF.GUI.ViewModels {
	public sealed record SelectionAssistantModeOption(SelectionAssistantMode Mode, string Label, string Description);
	public sealed record SelectionAssistantRuleOption(
		SelectionAssistantRuleKind Kind,
		string Label,
		bool NeedsValue = false,
		string Placeholder = "");

	public sealed class SelectionAssistantRuleVM : ReactiveObject {
		readonly SelectionAssistantVM owner;

		internal SelectionAssistantRuleVM(SelectionAssistantVM owner, SelectionAssistantRuleData data) {
			this.owner = owner;
			Data = data;
			Data.PropertyChanged += (_, _) => owner.InvalidatePreview();
			MoveUpCommand = ReactiveCommand.Create(() => owner.MoveRule(this, -1));
			MoveDownCommand = ReactiveCommand.Create(() => owner.MoveRule(this, +1));
			RemoveCommand = ReactiveCommand.Create(() => owner.RemoveRule(this));
		}

		public SelectionAssistantRuleData Data { get; }
		public IReadOnlyList<SelectionAssistantRuleOption> KindOptions => SelectionAssistantVM.RuleOptions;

		public SelectionAssistantRuleOption SelectedKind {
			get => KindOptions.First(option => option.Kind == Data.Kind);
			set {
				if (value == null || value.Kind == Data.Kind)
					return;
				Data.Kind = value.Kind;
				this.RaisePropertyChanged();
				this.RaisePropertyChanged(nameof(NeedsValue));
				this.RaisePropertyChanged(nameof(ValuePlaceholder));
				owner.InvalidatePreview();
			}
		}

		public bool NeedsValue => SelectedKind.NeedsValue;
		public string ValuePlaceholder => SelectedKind.Placeholder;

		public ReactiveCommand<Unit, Unit> MoveUpCommand { get; }
		public ReactiveCommand<Unit, Unit> MoveDownCommand { get; }
		public ReactiveCommand<Unit, Unit> RemoveCommand { get; }
	}

	public sealed class SelectionAssistantVM : ReactiveObject {
		readonly SelectionAssistantView host;
		readonly MainWindowVM main;
		SelectionAssistantData working;

		internal static readonly IReadOnlyList<SelectionAssistantRuleOption> RuleOptions = new[] {
			new SelectionAssistantRuleOption(SelectionAssistantRuleKind.NonBest, "非 BEST 优先删除"),
			new SelectionAssistantRuleOption(SelectionAssistantRuleKind.KeepPathContaining, "路径命中关键词优先保留", true, "每行一个路径关键词，例如：\\Master\\  或  Y:\\精选"),
			new SelectionAssistantRuleOption(SelectionAssistantRuleKind.DeletePathContaining, "路径命中关键词优先删除", true, "每行一个路径关键词，例如：temp / backup / cache"),
			new SelectionAssistantRuleOption(SelectionAssistantRuleKind.DeleteFileNameContaining, "文件名命中关键词优先删除", true, "每行一个文件名关键词，例如：copy / 副本 / (1)"),
			new SelectionAssistantRuleOption(SelectionAssistantRuleKind.LowerResolution, "分辨率较低优先删除"),
			new SelectionAssistantRuleOption(SelectionAssistantRuleKind.LowerBitrate, "视频码率较低优先删除"),
			new SelectionAssistantRuleOption(SelectionAssistantRuleKind.LowerFps, "帧率较低优先删除"),
			new SelectionAssistantRuleOption(SelectionAssistantRuleKind.ShorterDuration, "时长较短优先删除"),
			new SelectionAssistantRuleOption(SelectionAssistantRuleKind.LowerAudioBitrate, "音频码率较低优先删除"),
			new SelectionAssistantRuleOption(SelectionAssistantRuleKind.SmallerFile, "文件较小优先删除"),
			new SelectionAssistantRuleOption(SelectionAssistantRuleKind.OlderCreated, "创建时间较旧优先删除"),
			new SelectionAssistantRuleOption(SelectionAssistantRuleKind.NewerCreated, "创建时间较新优先删除"),
			new SelectionAssistantRuleOption(SelectionAssistantRuleKind.LongerPath, "完整路径较长优先删除"),
			new SelectionAssistantRuleOption(SelectionAssistantRuleKind.LongerFileName, "文件名较长优先删除"),
			new SelectionAssistantRuleOption(SelectionAssistantRuleKind.DeeperFolder, "目录层级较深优先删除"),
		};

		public IReadOnlyList<SelectionAssistantModeOption> ModeOptions { get; } = new[] {
			new SelectionAssistantModeOption(
				SelectionAssistantMode.AllButOne,
				"每组保留 1 个，其余勾选",
				"规则只负责决定保留谁；规则完全打平时仍会确定性地保留一个。"),
			new SelectionAssistantModeOption(
				SelectionAssistantMode.RulesOnly,
				"只勾选规则明确判定更差的副本",
				"如果所有启用规则都无法区分两个副本，它们保持未勾选，适合保守清理。"),
		};

		SelectionAssistantModeOption _SelectedMode = null!;
		public SelectionAssistantModeOption SelectedMode {
			get => _SelectedMode;
			set {
				if (value == null) return;
				this.RaiseAndSetIfChanged(ref _SelectedMode, value);
				working.Mode = value.Mode;
				this.RaisePropertyChanged(nameof(ModeDescription));
				InvalidatePreview();
			}
		}
		public string ModeDescription => SelectedMode?.Description ?? string.Empty;

		bool _CurrentFilterOnly;
		public bool CurrentFilterOnly {
			get => _CurrentFilterOnly;
			set {
				this.RaiseAndSetIfChanged(ref _CurrentFilterOnly, value);
				working.CurrentFilterOnly = value;
				InvalidatePreview();
			}
		}

		bool _PreserveExistingSelection;
		public bool PreserveExistingSelection {
			get => _PreserveExistingSelection;
			set {
				this.RaiseAndSetIfChanged(ref _PreserveExistingSelection, value);
				working.PreserveExistingSelection = value;
				InvalidatePreview();
			}
		}

		public ObservableCollection<SelectionAssistantRuleVM> Rules { get; } = new();

		string _PreviewStatus = "点击“预览”查看本次会勾选多少文件。";
		public string PreviewStatus {
			get => _PreviewStatus;
			private set => this.RaiseAndSetIfChanged(ref _PreviewStatus, value);
		}

		string _PreviewDetail = "规则按从上到下的优先级比较；第一条能区分候选的规则优先。";
		public string PreviewDetail {
			get => _PreviewDetail;
			private set => this.RaiseAndSetIfChanged(ref _PreviewDetail, value);
		}

		public ReactiveCommand<Unit, Unit> AddRuleCommand { get; }
		public ReactiveCommand<Unit, Unit> ResetRulesCommand { get; }
		public ReactiveCommand<Unit, Unit> PreviewCommand { get; }
		public ReactiveCommand<Unit, Unit> ApplyCommand { get; }
		public ReactiveCommand<Unit, Unit> CloseCommand { get; }

		internal SelectionAssistantVM(SelectionAssistantView host, MainWindowVM main) {
			this.host = host;
			this.main = main;
			working = SettingsFile.Instance.SelectionAssistant?.Clone() ?? new SelectionAssistantData();
			if (working.Rules.Count == 0)
				working.Rules = SelectionAssistantData.CreateDefaultRules();

			_CurrentFilterOnly = working.CurrentFilterOnly;
			_PreserveExistingSelection = working.PreserveExistingSelection;
			_SelectedMode = ModeOptions.First(option => option.Mode == working.Mode);
			RebuildRules();

			AddRuleCommand = ReactiveCommand.Create(() => {
				var data = new SelectionAssistantRuleData {
					Kind = SelectionAssistantRuleKind.DeletePathContaining,
					Value = string.Empty,
				};
				working.Rules.Add(data);
				Rules.Add(new SelectionAssistantRuleVM(this, data));
				InvalidatePreview();
			});
			ResetRulesCommand = ReactiveCommand.Create(() => {
				working.Rules = SelectionAssistantData.CreateDefaultRules();
				RebuildRules();
				InvalidatePreview();
			});
			PreviewCommand = ReactiveCommand.Create(Preview);
			ApplyCommand = ReactiveCommand.Create(Apply);
			CloseCommand = ReactiveCommand.Create(() => host.Close());
		}

		void Preview() {
			SelectionAssistantData data = BuildData();
			SelectionAssistantPlan plan = main.PreviewSelectionAssistant(data);
			UpdateStatus(plan, applied: false);
		}

		void Apply() {
			SelectionAssistantData data = BuildData();
			SelectionAssistantPlan plan = main.RunSelectionAssistant(data);
			SettingsFile.Instance.SelectionAssistant = data.Clone();
			SettingsFile.SaveSettings();
			UpdateStatus(plan, applied: true);
		}

		void UpdateStatus(SelectionAssistantPlan plan, bool applied) {
			string verb = applied ? "已应用" : "预览";
			if (plan.ActiveRules == 0) {
				PreviewStatus = "没有可用规则：请至少启用一条规则；关键词规则还需要填写关键词。";
				PreviewDetail = "未修改任何勾选。";
				return;
			}
			PreviewStatus = $"{verb}：处理 {plan.ProcessedGroups:N0} 组；{plan.GroupsWithMarks:N0} 组会产生勾选；共 {plan.ToCheck.Count:N0} 个文件，约 {plan.SelectedBytes.BytesToString()}。";
			PreviewDetail = plan.TieBreakSelections > 0
				? $"其中 {plan.TiedGroups:N0} 组存在规则完全打平，{plan.TieBreakSelections:N0} 个勾选来自“每组只留一个”的最终兜底。想更保守可切换到“只勾选规则明确判定更差”。每个已处理组始终强制留 1 个未勾选副本。"
				: "所有自动勾选都由当前规则顺序明确区分；每个已处理组始终强制留 1 个未勾选副本。";
		}

		internal void InvalidatePreview() {
			PreviewStatus = "规则已修改，点击“预览”重新计算。";
			PreviewDetail = "勾选表示待处理/待删除；应用后可用 Ctrl+Z 撤销整个选择助手操作。";
		}

		internal void MoveRule(SelectionAssistantRuleVM rule, int delta) {
			int index = Rules.IndexOf(rule);
			int target = index + delta;
			if (index < 0 || target < 0 || target >= Rules.Count)
				return;
			Rules.Move(index, target);
			working.Rules.Move(index, target);
			InvalidatePreview();
		}

		internal void RemoveRule(SelectionAssistantRuleVM rule) {
			int index = Rules.IndexOf(rule);
			if (index < 0)
				return;
			Rules.RemoveAt(index);
			working.Rules.RemoveAt(index);
			InvalidatePreview();
		}

		void RebuildRules() {
			Rules.Clear();
			foreach (SelectionAssistantRuleData rule in working.Rules)
				Rules.Add(new SelectionAssistantRuleVM(this, rule));
		}

		SelectionAssistantData BuildData() => new() {
			Mode = SelectedMode.Mode,
			CurrentFilterOnly = CurrentFilterOnly,
			PreserveExistingSelection = PreserveExistingSelection,
			Rules = new ObservableCollection<SelectionAssistantRuleData>(Rules.Select(rule => rule.Data.Clone())),
		};
	}
}

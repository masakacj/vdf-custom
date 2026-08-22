// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Collections.ObjectModel;
using ReactiveUI;

namespace VDF.GUI.Data {
	public enum SelectionAssistantMode {
		AllButOne,
		RulesOnly,
	}

	/// <summary>
	/// Ordered preference rules for Selection Assistant. The planner compares candidates
	/// from top to bottom; the first rule that differentiates two candidates wins. Every
	/// rule is phrased as "more deletable" so the least-deletable candidate becomes the
	/// keeper.
	/// </summary>
	public enum SelectionAssistantRuleKind {
		NonBest,
		KeepPathContaining,
		DeletePathContaining,
		DeleteFileNameContaining,
		LowerResolution,
		LowerBitrate,
		LowerFps,
		ShorterDuration,
		LowerAudioBitrate,
		SmallerFile,
		OlderCreated,
		NewerCreated,
		LongerPath,
		LongerFileName,
		DeeperFolder,
	}

	public sealed class SelectionAssistantRuleData : ReactiveObject {
		bool _Enabled = true;
		public bool Enabled {
			get => _Enabled;
			set => this.RaiseAndSetIfChanged(ref _Enabled, value);
		}

		SelectionAssistantRuleKind _Kind;
		public SelectionAssistantRuleKind Kind {
			get => _Kind;
			set => this.RaiseAndSetIfChanged(ref _Kind, value);
		}

		string _Value = string.Empty;
		/// <summary>Keyword list used by path/name rules. Newline/comma/semicolon separated.</summary>
		public string Value {
			get => _Value;
			set => this.RaiseAndSetIfChanged(ref _Value, value ?? string.Empty);
		}

		public SelectionAssistantRuleData Clone() => new() {
			Enabled = Enabled,
			Kind = Kind,
			Value = Value,
		};
	}

	public sealed class SelectionAssistantData : ReactiveObject {
		SelectionAssistantMode _Mode = SelectionAssistantMode.AllButOne;
		public SelectionAssistantMode Mode {
			get => _Mode;
			set => this.RaiseAndSetIfChanged(ref _Mode, value);
		}

		bool _CurrentFilterOnly = true;
		/// <summary>When true, hidden rows are never changed and BEST is computed from visible rows.</summary>
		public bool CurrentFilterOnly {
			get => _CurrentFilterOnly;
			set => this.RaiseAndSetIfChanged(ref _CurrentFilterOnly, value);
		}

		bool _PreserveExistingSelection;
		/// <summary>
		/// Add the assistant's marks instead of replacing marks inside processed groups.
		/// The chosen keeper is still explicitly unchecked as a safety guarantee.
		/// </summary>
		public bool PreserveExistingSelection {
			get => _PreserveExistingSelection;
			set => this.RaiseAndSetIfChanged(ref _PreserveExistingSelection, value);
		}

		ObservableCollection<SelectionAssistantRuleData> _Rules = CreateDefaultRules();
		public ObservableCollection<SelectionAssistantRuleData> Rules {
			get => _Rules;
			set => this.RaiseAndSetIfChanged(ref _Rules, value ?? new());
		}

		public SelectionAssistantData Clone() => new() {
			Mode = Mode,
			CurrentFilterOnly = CurrentFilterOnly,
			PreserveExistingSelection = PreserveExistingSelection,
			Rules = new ObservableCollection<SelectionAssistantRuleData>(Rules.Select(rule => rule.Clone())),
		};

		public static ObservableCollection<SelectionAssistantRuleData> CreateDefaultRules() => new(new[] {
			new SelectionAssistantRuleData { Kind = SelectionAssistantRuleKind.DeletePathContaining, Value = "temp\ncache\nbackup" },
			new SelectionAssistantRuleData { Kind = SelectionAssistantRuleKind.NonBest },
			new SelectionAssistantRuleData { Kind = SelectionAssistantRuleKind.LowerResolution },
			new SelectionAssistantRuleData { Kind = SelectionAssistantRuleKind.LowerBitrate },
			new SelectionAssistantRuleData { Kind = SelectionAssistantRuleKind.LowerFps },
			new SelectionAssistantRuleData { Kind = SelectionAssistantRuleKind.SmallerFile },
		});
	}
}

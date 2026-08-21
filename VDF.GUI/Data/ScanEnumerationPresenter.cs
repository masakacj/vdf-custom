// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Collections.ObjectModel;
using ReactiveUI;
using VDF.Core.Utils;

namespace VDF.GUI.Data {
	/// <summary>One include-root row shown while VDF builds the scan file list.</summary>
	public sealed class ScanEnumerationRow : ReactiveObject {
		internal ScanEnumerationRow(string rootPath) => RootPath = rootPath;

		public string RootPath { get; }

		string backendLabel = "检测中";
		public string BackendLabel {
			get => backendLabel;
			private set => this.RaiseAndSetIfChanged(ref backendLabel, value);
		}

		bool isEverything;
		public bool IsEverything {
			get => isEverything;
			private set => this.RaiseAndSetIfChanged(ref isEverything, value);
		}

		string stat = "检测 Everything 索引…";
		public string Stat {
			get => stat;
			private set => this.RaiseAndSetIfChanged(ref stat, value);
		}

		string detail = string.Empty;
		public string Detail {
			get => detail;
			private set => this.RaiseAndSetIfChanged(ref detail, value);
		}

		internal void Update(FileEnumerationReport report) {
			BackendLabel = report.Backend switch {
				FileEnumerationBackend.EverythingIpc => "⚡ Everything IPC",
				FileEnumerationBackend.NativeFileSystem => "文件系统",
				_ => "检测中",
			};
			IsEverything = report.Backend == FileEnumerationBackend.EverythingIpc;
			Detail = report.Detail;
			if (report.IsCompleted) {
				double seconds = Math.Max(0, report.Elapsed.TotalSeconds);
				Stat = $"{report.FileCount:N0} 文件 · {seconds:0.0} 秒";
			}
			else {
				Stat = report.Backend switch {
					FileEnumerationBackend.NativeFileSystem => "正在遍历目录…",
					FileEnumerationBackend.EverythingIpc => "正在查询索引…",
					_ => "检测 Everything 索引…",
				};
			}
		}
	}

	public sealed class ScanEnumerationPresenter : ReactiveObject {
		public ObservableCollection<ScanEnumerationRow> Rows { get; } = new();

		bool hasRows;
		public bool HasRows {
			get => hasRows;
			private set => this.RaiseAndSetIfChanged(ref hasRows, value);
		}

		public void Update(FileEnumerationReport report) {
			ScanEnumerationRow? row = Rows.FirstOrDefault(item =>
				item.RootPath.Equals(report.RootPath, StringComparison.OrdinalIgnoreCase));
			if (row == null) {
				row = new ScanEnumerationRow(report.RootPath);
				Rows.Add(row);
			}
			row.Update(report);
			HasRows = Rows.Count > 0;
		}

		public void Clear() {
			Rows.Clear();
			HasRows = false;
		}
	}
}

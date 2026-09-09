using VDF.GUI.Utils;

namespace VDF.GUI.Tests {
	public class SingleInstanceCoordinatorTests {
		[Fact]
		public void WindowsSecondLaunch_IsRejected_AndPrimaryCanBeReacquiredAfterDispose() {
			if (!OperatingSystem.IsWindows())
				return;

			var first = SingleInstanceCoordinator.TryAcquirePrimary();
			Assert.NotNull(first);
			try {
				var second = SingleInstanceCoordinator.TryAcquirePrimary();
				Assert.Null(second);
			}
			finally {
				first!.Dispose();
			}

			using var third = SingleInstanceCoordinator.TryAcquirePrimary();
			Assert.NotNull(third);
		}
	}
}

using System.Globalization;
using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Presentation.Services;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 连接诊断窗口的步骤名、副标题与导出报告跟随界面语言。
/// </summary>
/// <remarks>
/// 这几处原先写死中文,切到英文照样是中文 —— 连导出的报告文件名都是「诊断报告-…」。
/// </remarks>
[TestClass]
[TestCategory("i18n")]
public class ConnectionDiagnosticsViewModelTests
{
    [TestMethod]
    public void EnglishUi_ShowsEnglishStepsSummaryAndReport()
    {
        CultureInfo original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new("en");
            var profile = new SessionProfile { Name = "web", Host = "web.example.com", Username = "root", Port = 22 };
            var vm = new ConnectionDiagnosticsViewModel(profile, Substitute.For<IConnectionDiagnosticsService>());

            Assert.AreEqual("1. DNS Resolution", vm.Steps[0].DisplayName);
            Assert.Contains("root@web.example.com:22", vm.TargetSummary);
            Assert.StartsWith("diagnostics-web-", vm.SuggestedReportFileName);
            string report = vm.BuildReportText();
            Assert.StartsWith("VelaShell Connection Diagnostics Report", report);
            Assert.Contains("[Not run]", report);
            Assert.DoesNotContain(c => c is >= '一' and <= '鿿', report, "英文界面下导出的报告里不该有中文:\n" + report);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }
}

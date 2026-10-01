using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Presentation.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 资源管理器的 Ctrl 双选:恰好两条、先选的在左、第三条把最早那条顶掉,以及它何时结束。
/// </summary>
[TestClass]
[TestCategory("SessionTree")]
public class SessionTreeDualSelectionTests
{
    private readonly ISessionRepository _repository = Substitute.For<ISessionRepository>();
    private SessionTreeViewModel _vm = null!;
    private SessionProfile _alpha = null!;
    private SessionProfile _beta = null!;
    private SessionProfile _gamma = null!;
    private SessionProfile _bucket = null!;
    private ServerGroup _group = null!;

    [TestInitialize]
    public async Task SetUp()
    {
        _group = new ServerGroup { Id = Guid.NewGuid(), Name = "Prod", SortOrder = 0 };
        _alpha = Profile("alpha", ConnectionType.SSH, _group.Id);
        _beta = Profile("beta", ConnectionType.SFTP, _group.Id);
        _gamma = Profile("gamma", ConnectionType.FTP, _group.Id);
        _bucket = Profile("bucket", ConnectionType.Plugin, _group.Id);
        _repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { _group }));
        _repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile> { _alpha, _beta, _gamma, _bucket }));
        _vm = new SessionTreeViewModel(_repository);
        await _vm.LoadCommand.Execute().FirstAsync();
    }

    private static SessionProfile Profile(string name, ConnectionType type, Guid groupId) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Host = $"{name}.example.com",
        Username = "admin",
        ConnectionType = type,
        GroupId = groupId,
    };

    private SessionTreeNodeViewModel Node(SessionProfile profile) =>
        _vm.Nodes.Single(n => n.IsGroup).Children.Single(c => c.Id == profile.Id);

    [TestMethod]
    public void CtrlClick_AfterAPlainSelection_PairsTheTwo_FirstOneOnTheLeft()
    {
        _vm.SelectedNode = Node(_alpha);

        _vm.ToggleDualSelection(Node(_beta));

        Assert.AreSequenceEqual([Node(_alpha), Node(_beta)], _vm.DualSelection.ToArray());
        Assert.AreEqual(1, Node(_alpha).DualSelectionOrder);
        Assert.AreEqual(2, Node(_beta).DualSelectionOrder);
        Assert.AreSame(Node(_beta), _vm.SelectedNode, "选中项落在最后点的那条上。");
        Assert.IsTrue(_vm.CanOpenDualSelection);
    }

    [TestMethod]
    public void CtrlClick_AThirdOne_DropsTheOldest_AndShiftsTheRest()
    {
        _vm.SelectedNode = Node(_alpha);
        _vm.ToggleDualSelection(Node(_beta));

        _vm.ToggleDualSelection(Node(_gamma));

        Assert.AreSequenceEqual([Node(_beta), Node(_gamma)], _vm.DualSelection.ToArray());
        Assert.AreEqual(0, Node(_alpha).DualSelectionOrder, "被顶掉的那条要取消选中。");
        Assert.AreEqual(1, Node(_beta).DualSelectionOrder);
        Assert.AreEqual(2, Node(_gamma).DualSelectionOrder);
    }

    [TestMethod]
    public void CtrlClick_OneOfThePair_FallsBackToAPlainSelectionOfTheOther()
    {
        _vm.SelectedNode = Node(_alpha);
        _vm.ToggleDualSelection(Node(_beta));

        _vm.ToggleDualSelection(Node(_beta));

        Assert.IsEmpty(_vm.DualSelection);
        Assert.AreEqual(0, Node(_beta).DualSelectionOrder);
        Assert.AreSame(Node(_alpha), _vm.SelectedNode);
    }

    [TestMethod]
    public void CtrlClick_TheOnlySelectedRow_DeselectsIt()
    {
        _vm.SelectedNode = Node(_alpha);

        _vm.ToggleDualSelection(Node(_alpha));

        Assert.IsEmpty(_vm.DualSelection);
        Assert.IsNull(_vm.SelectedNode);
    }

    [TestMethod]
    public void CtrlClick_WithNothingSelected_JustSelectsTheRow()
    {
        _vm.ToggleDualSelection(Node(_alpha));

        Assert.IsEmpty(_vm.DualSelection, "一条不成双选,就是普通单选。");
        Assert.AreSame(Node(_alpha), _vm.SelectedNode);
    }

    [TestMethod]
    public void CtrlClick_AGroupRow_IsIgnored()
    {
        _vm.SelectedNode = Node(_alpha);

        _vm.ToggleDualSelection(_vm.Nodes.Single(n => n.IsGroup));

        Assert.IsEmpty(_vm.DualSelection);
        Assert.AreSame(Node(_alpha), _vm.SelectedNode);
    }

    [TestMethod]
    public void SelectingARowOutsideThePair_EndsTheDualSelection()
    {
        _vm.SelectedNode = Node(_alpha);
        _vm.ToggleDualSelection(Node(_beta));

        _vm.SelectedNode = Node(_gamma);

        Assert.IsEmpty(_vm.DualSelection);
        Assert.AreEqual(0, Node(_alpha).DualSelectionOrder);
        Assert.AreEqual(0, Node(_beta).DualSelectionOrder);
    }

    [TestMethod]
    public void CollapsingTheGroup_EndsTheDualSelection()
    {
        _vm.SelectedNode = Node(_alpha);
        _vm.ToggleDualSelection(Node(_beta));

        _vm.Nodes.Single(n => n.IsGroup).IsExpanded = false;

        Assert.IsEmpty(_vm.DualSelection, "收进折叠分组里看不见的行不该还算选中。");
    }

    [TestMethod]
    public async Task OpenDualSftp_RaisesBothProfiles_LeftFirst()
    {
        _vm.SelectedNode = Node(_gamma);
        _vm.ToggleDualSelection(Node(_alpha));
        (SessionProfile Left, SessionProfile Right)? raised = null;
        _vm.OpenDualSftpRequested += (left, right) => raised = (left, right);

        await _vm.OpenDualSftpCommand.Execute().FirstAsync();

        Assert.IsNotNull(raised);
        Assert.AreSame(_gamma, raised.Value.Left);
        Assert.AreSame(_alpha, raised.Value.Right);
    }

    [TestMethod]
    public async Task APluginFileProtocolInThePair_CanBeOpenedSideBySide()
    {
        // S3 这类插件文件协议可以进双栏(与 SFTP / FTP 互相搬文件)。
        _vm.SelectedNode = Node(_alpha);
        _vm.ToggleDualSelection(Node(_bucket));

        Assert.IsTrue(_vm.CanOpenDualSelection);
        Assert.IsTrue(await _vm.OpenDualSftpCommand.CanExecute.FirstAsync());
    }

    [TestMethod]
    public async Task TheHostFilter_CanRuleAPairOut()
    {
        // 工作台类插件(Redis…)由宿主判断排掉 —— 树只认得连接类型,问不到插件注册表。
        _vm.DualSftpFilter = profile => profile.ConnectionType != ConnectionType.Plugin;
        _vm.SelectedNode = Node(_alpha);
        _vm.ToggleDualSelection(Node(_bucket));

        Assert.IsTrue(_vm.HasDualSelection);
        Assert.IsFalse(_vm.CanOpenDualSelection);
        Assert.IsFalse(await _vm.OpenDualSftpCommand.CanExecute.FirstAsync());
    }
}

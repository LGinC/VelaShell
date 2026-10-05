using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

// 断言一律比对本地化资源,不写中文字面量(同 AuthenticationDialogViewModelTests)。
// 用户靠「用途」与「目的主机」两行分辨自己刚敲的 git pull 与「有人拿这把钥登录别处」,
// 这一组盯的是三种情形各自说对了话,而且陌生的目的主机是用警告色摆出来的。
[TestClass]
public sealed class AgentSignPromptViewModelTests
{
    private static readonly AgentSignRequest Base =
        new("joe@jump:22", "ssh-ed25519", "SHA256:key", "~/.ssh/id_ed25519", TimeSpan.FromSeconds(60));

    [TestMethod]
    public void Login_ToKnownHost_ShowsUserAndHostNames()
    {
        var vm = new AgentSignPromptViewModel(Base with
        {
            LoginUser = "git",
            DestinationFingerprint = "SHA256:dest",
            DestinationHosts = ["github.com", "10.0.0.9:2222"],
        });

        Assert.IsTrue(vm.IsLogin);
        Assert.AreEqual(Strings.Format("AgentSign_PurposeLogin", "git"), vm.Purpose);
        Assert.AreEqual("github.com, 10.0.0.9:2222", vm.Destination);
        Assert.IsFalse(vm.IsDestinationUnfamiliar);
    }

    [TestMethod]
    public void Login_ToUnknownHost_ShowsFingerprintAsUnfamiliar()
    {
        var vm = new AgentSignPromptViewModel(Base with { LoginUser = "root", DestinationFingerprint = "SHA256:dest" });

        Assert.AreEqual(Strings.Format("AgentSign_DestinationNotKnown", "SHA256:dest"), vm.Destination);
        Assert.IsTrue(vm.IsDestinationUnfamiliar);
    }

    [TestMethod]
    public void Login_WithUnverifiedDestination_SaysSo()
    {
        var vm = new AgentSignPromptViewModel(Base with { LoginUser = "root" });

        Assert.AreEqual(Strings.Get("AgentSign_DestinationUnverified"), vm.Destination);
        Assert.IsTrue(vm.IsDestinationUnfamiliar);
    }

    [TestMethod]
    public void SshSig_ShowsNamespace_AndNoDestinationRow()
    {
        var vm = new AgentSignPromptViewModel(Base with { SignatureNamespace = "git" });

        Assert.IsFalse(vm.IsLogin);
        Assert.AreEqual(Strings.Format("AgentSign_PurposeSshSig", "git"), vm.Purpose);
    }

    [TestMethod]
    public void UnrecognizedData_SaysItIsNotALogin()
    {
        var vm = new AgentSignPromptViewModel(Base);

        Assert.IsFalse(vm.IsLogin);
        Assert.AreEqual(Strings.Get("AgentSign_PurposeUnknown"), vm.Purpose);
    }
}

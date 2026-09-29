using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Auth;

namespace VelaShell.Infrastructure.Tests.Ssh;

/// <summary>
/// keyboard-interactive 的应答:认得出的口令提示用已有的密码代答一次,其余交给界面;
/// 非密码认证不回退到口令;纯展示的一轮不弹框;对端文字先清洗再上界面。
/// </summary>
/// <remarks>
/// 真实 OpenSSH + PAM 上的端到端用例在 <c>VelaShell.Core.Tests</c> 的 <c>KeyboardInteractiveIntegrationTests</c>。
/// </remarks>
[TestClass]
[TestCategory("Ssh")]
public sealed class KeyboardInteractiveResponderTests
{
    private const string Target = "root@bastion:22";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task PamPasswordThenCode_ThePasswordIsAnsweredAndOnlyTheCodeIsAsked()
    {
        var prompt = new ScriptedPrompt(_ => ["123456"]);
        var responder = new KeyboardInteractiveResponder(prompt, Target, "hunter2");

        CollectionAssert.AreEqual(new[] { "hunter2" }, (await Respond(responder, Round(("Password: ", false)))).ToArray());
        CollectionAssert.AreEqual(new[] { "123456" }, (await Respond(responder, Round(("Verification code: ", false)))).ToArray());

        KeyboardInteractiveRequest asked = prompt.Requests.Single();
        Assert.AreEqual(Target, asked.Target);
        Assert.AreEqual(new KeyboardInteractiveField("Verification code:", false), asked.Fields.Single());
    }

    [TestMethod]
    public async Task ASecondPasswordRound_IsAskedRatherThanRepeatingTheSamePassword()
    {
        // 同一次认证里再问口令(改密码流程的 New password:、或者刚才那个不对):交给用户
        var prompt = new ScriptedPrompt(_ => ["new-secret"]);
        var responder = new KeyboardInteractiveResponder(prompt, Target, "hunter2");

        await Respond(responder, Round(("Password: ", false)));
        CollectionAssert.AreEqual(new[] { "new-secret" }, (await Respond(responder, Round(("New password: ", false)))).ToArray());
        Assert.HasCount(1, prompt.Requests);
    }

    [TestMethod]
    public async Task AnEmptySavedPassword_IsAskedFor()
    {
        var prompt = new ScriptedPrompt(_ => ["typed"]);
        var responder = new KeyboardInteractiveResponder(prompt, Target, "");

        CollectionAssert.AreEqual(new[] { "typed" }, (await Respond(responder, Round(("Password: ", false)))).ToArray());
    }

    [TestMethod]
    [DataRow("Password + OTP: ")]
    [DataRow("Password and verification code: ")]
    [DataRow("密码+动态码:")]
    public async Task APasswordPromptThatAlsoWantsACode_IsAsked(string text)
    {
        var prompt = new ScriptedPrompt(_ => ["hunter2123456"]);
        var responder = new KeyboardInteractiveResponder(prompt, Target, "hunter2");

        await Respond(responder, Round((text, false)));

        Assert.HasCount(1, prompt.Requests, "要的是口令与动态码拼起来的串,不能只填口令");
    }

    [TestMethod]
    public async Task KeyBasedAuthentication_DeclinesALonePasswordPrompt_ButAsksForTheCode()
    {
        // 私钥 / 证书 / agent:钥被拒之后冒出来的「Password:」不回退成口令认证,答空串让服务端拒掉
        var prompt = new ScriptedPrompt(_ => ["654321"]);
        var responder = new KeyboardInteractiveResponder(prompt, Target, password: null);

        CollectionAssert.AreEqual(new[] { "" }, (await Respond(responder, Round(("Password: ", false)))).ToArray());
        Assert.IsEmpty(prompt.Requests);

        CollectionAssert.AreEqual(new[] { "654321" }, (await Respond(responder, Round(("Verification code: ", false)))).ToArray());
    }

    [TestMethod]
    public async Task EchoedAndMultiPromptRounds_AreAskedAsIs()
    {
        var prompt = new ScriptedPrompt(r => [.. r.Fields.Select((_, i) => $"a{i}")]);
        var responder = new KeyboardInteractiveResponder(prompt, Target, "hunter2");

        IReadOnlyList<string> answers = await Respond(responder,
            Round(("Passcode or option (1-3): ", true), ("PIN: ", false)), name: "Duo", instruction: "Choose a method");

        CollectionAssert.AreEqual(new[] { "a0", "a1" }, answers.ToArray());
        KeyboardInteractiveRequest asked = prompt.Requests.Single();
        Assert.AreEqual("Duo", asked.Name);
        Assert.AreEqual("Choose a method", asked.Instruction);
        Assert.IsTrue(asked.Fields[0].Echo);
        Assert.IsFalse(asked.Fields[1].Echo);
    }

    [TestMethod]
    public async Task InformationalRounds_AreNotAsked_AndTheirTextShowsUpInTheNextPrompt()
    {
        var prompt = new ScriptedPrompt(_ => ["123456"]);
        var responder = new KeyboardInteractiveResponder(prompt, Target, "hunter2");

        IReadOnlyList<string> none = await Respond(responder, Round(), instruction: "Approve the push on your phone.");
        Assert.IsEmpty(none);
        Assert.IsEmpty(prompt.Requests, "纯展示的一轮不该弹框要人输入(规格 04 §6.4)");

        await Respond(responder, Round(("Verification code: ", false)), instruction: "Or type a code.");
        Assert.AreEqual("Approve the push on your phone.\nOr type a code.", prompt.Requests.Single().Instruction);
    }

    [TestMethod]
    public async Task UserCancel_IsRememberedAndThrownAsCancelled()
    {
        var responder = new KeyboardInteractiveResponder(new ScriptedPrompt(_ => null), Target, "hunter2");

        await Assert.ThrowsExactlyAsync<VelaSshAuthenticationCancelledException>(
            () => Respond(responder, Round(("Verification code: ", false))));
        Assert.IsTrue(responder.Cancelled);
    }

    [TestMethod]
    public async Task ATriggeredToken_IsACancellation_NotAUserCancel()
    {
        using var cts = new CancellationTokenSource();
        var prompt = new ScriptedPrompt(_ =>
        {
            cts.Cancel();
            return null;
        });
        var responder = new KeyboardInteractiveResponder(prompt, Target, "hunter2");

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => responder.RespondAsync(Challenge(Round(("Verification code: ", false))), cts.Token).AsTask());
        Assert.IsFalse(responder.Cancelled, "关了标签 / 认证超时不是用户在框上点的取消");
    }

    [TestMethod]
    public async Task AWrongNumberOfAnswers_IsRejectedHere_NotSentToTheServer()
    {
        var responder = new KeyboardInteractiveResponder(new ScriptedPrompt(_ => ["only-one"]), Target, "hunter2");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => Respond(responder, Round(("A: ", true), ("B: ", true))));
    }

    [TestMethod]
    public async Task PeerText_IsCleanedBeforeItReachesTheScreen()
    {
        var prompt = new ScriptedPrompt(_ => ["x"]);
        var responder = new KeyboardInteractiveResponder(prompt, Target, "hunter2");

        await Respond(responder, Round(("\u001b[31mCode\u202E:\u0007 ", false)),
            name: "Bank\u0000", instruction: "line1\r\nline2\t" + new string('x', 5000));

        KeyboardInteractiveRequest asked = prompt.Requests.Single();
        Assert.AreEqual("[31mCode:", asked.Fields[0].Prompt, "ESC、BEL 与 RLO 这类控制符要去掉");
        Assert.AreEqual("Bank", asked.Name);
        StringAssert.StartsWith(asked.Instruction, "line1\nline2 ");
        Assert.AreEqual(2048, asked.Instruction.Length);
        StringAssert.EndsWith(asked.Instruction, "…");
    }

    [TestMethod]
    [DataRow("Password:", true)]
    [DataRow("root@host's password: ", true)]
    [DataRow("(current) UNIX password: ", true)]
    [DataRow("请输入密码:", true)]
    [DataRow("パスワード:", true)]
    [DataRow("비밀번호:", true)]
    [DataRow("Verification code: ", false)]
    [DataRow("Passcode or option (1-3): ", false)]
    [DataRow("One-time password (OATH) for `root': ", false)]
    [DataRow("Enter PIN: ", false)]
    [DataRow("请输入验证码:", false)]
    public void LooksLikePasswordPrompt(string text, bool expected) =>
        Assert.AreEqual(expected, KeyboardInteractiveResponder.LooksLikePasswordPrompt(text), text);

    [TestMethod]
    public async Task TheCredentialWrapsTheResponder()
    {
        var prompt = new ScriptedPrompt(_ => ["123456"]);
        KeyboardInteractiveCredential credential = new KeyboardInteractiveResponder(prompt, Target, "hunter2").ToCredential();

        IReadOnlyList<string> answers = await credential.RespondAsync(
            Challenge(Round(("Verification code: ", false))), TestContext.CancellationToken);

        CollectionAssert.AreEqual(new[] { "123456" }, answers.ToArray());
        Assert.AreEqual("keyboard-interactive", credential.MethodName);
    }

    private Task<IReadOnlyList<string>> Respond(KeyboardInteractiveResponder responder,
        SshKeyboardPrompt[] prompts,
        string name = "",
        string instruction = "") =>
        responder.RespondAsync(Challenge(prompts, name, instruction), TestContext.CancellationToken).AsTask();

    private static SshKeyboardPrompt[] Round(params (string Text, bool Echo)[] prompts) =>
        [.. prompts.Select(p => new SshKeyboardPrompt(p.Text, p.Echo))];

    private static SshKeyboardChallenge Challenge(SshKeyboardPrompt[] prompts, string name = "", string instruction = "") =>
        new() { Name = name, Instruction = instruction, Prompts = prompts };

    private sealed class ScriptedPrompt(Func<KeyboardInteractiveRequest, IReadOnlyList<string>?> answer) : IKeyboardInteractivePrompt
    {
        public List<KeyboardInteractiveRequest> Requests { get; } = [];

        public Task<IReadOnlyList<string>?> AskAsync(KeyboardInteractiveRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }
}

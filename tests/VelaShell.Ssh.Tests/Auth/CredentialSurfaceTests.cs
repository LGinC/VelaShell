// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/04-authentication.md §2.2

using System.Reflection;
using VelaShell.Ssh.Auth;

namespace VelaShell.Ssh.Tests.Auth;

/// <summary>凭据的公开面：只有认证器认识的那几种，使用者写不出一条「永远不会生效」的凭据。</summary>
[TestClass]
[TestCategory("Auth")]
public sealed class CredentialSurfaceTests
{
    /// <summary>
    /// 库外不能继承 <see cref="SshCredential"/>：认证器只认识库里的几种，外部子类曾经被当成「取不到材料」静默跳过。
    /// <c>none</c> 由认证器自己发，不对外。
    /// </summary>
    [TestMethod]
    public void 凭据只有库里的几种()
    {
        ConstructorInfo[] constructors = typeof(SshCredential).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.IsTrue(constructors.All(c => c.IsFamilyAndAssembly), "SshCredential 的构造函数应当是 private protected");
        Assert.IsFalse(typeof(NoneCredential).IsPublic, "NoneCredential 不该公开");
    }
}

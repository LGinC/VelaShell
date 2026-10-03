using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Resources;

namespace VelaShell.Infrastructure.Credentials;

/// <summary>
/// 本机共享凭据(设置 → 共享凭据)这一来源:按引用里的 Guid 从仓储取一条(#550)。
/// </summary>
/// <remarks>
/// 没有外部进程、没有解锁、也没有超时 —— 读一次本地库。所以设计里「自动重连不许弹解锁框」
/// 那条交互与非交互的区分对它不起作用,解析器也就不必为它传那个选项。
/// </remarks>
public sealed class SharedCredentialProvider(ISharedCredentialRepository repository) : ICredentialProvider
{
    private readonly ISharedCredentialRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    /// <inheritdoc />
    public string Id => CredentialReference.SharedProviderId;

    /// <inheritdoc />
    public async Task<ResolvedCredential> ResolveAsync(CredentialReference reference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        if (!reference.TryGetSharedId(out Guid id)
            || await _repository.GetAsync(id).ConfigureAwait(false) is not { } credential)
        {
            throw new CredentialProviderException(CredentialFailure.NotFound, Id, Strings.Get("Cred_Failure_NotFound"));
        }
        return new(
            credential.Name,
            credential.Username,
            credential.AuthMethod,
            credential.Password,
            credential.PrivateKeyPath,
            credential.PrivateKeyPassphrase,
            credential.CertificatePath);
    }
}

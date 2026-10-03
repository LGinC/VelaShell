namespace VelaShell.Core.Credentials;

/// <summary>
/// 连接配置里指向一条凭据的引用:配置只记「去哪取」,连接那一刻才取(#550)。
/// </summary>
/// <remarks>
/// <para>
/// 引用本身不是机密,明文落盘、随云同步漫游。它的意义在于<b>间接</b>:一百条连接引用同一条凭据,
/// 改密码只改那一条,下一次连接就都用上新的 —— 而不是一百条配置各存一份副本、改一百次。
/// </para>
/// <para>
/// 两段式(来源 + 条目)而不是只存一个 Guid:凭据来源不止本机的共享凭据一种,
/// 设计中的外部密码管理器(velashell-docs《凭据管理器集成设计》)走的是同一个引用形状。
/// </para>
/// </remarks>
public sealed record CredentialReference
{
    /// <summary>本机共享凭据(设置 → 共享凭据)的来源 id。落盘在引用里,定下后不可改。</summary>
    public const string SharedProviderId = "shared";

    /// <summary>凭据来源的 id(如 <see cref="SharedProviderId" />)。</summary>
    public required string ProviderId { get; init; }

    /// <summary>条目在该来源里的标识;共享凭据为其 Guid 的 <c>D</c> 格式。</summary>
    public required string ItemId { get; init; }

    /// <summary>构造一条指向本机共享凭据的引用。</summary>
    /// <param name="credentialId">共享凭据的 Id。</param>
    /// <returns>引用。</returns>
    public static CredentialReference ForShared(Guid credentialId) =>
        new() { ProviderId = SharedProviderId, ItemId = credentialId.ToString("D") };

    /// <summary>若这是一条指向本机共享凭据的引用,取出凭据 Id。</summary>
    /// <remarks>写成方法而不是属性:属性会被序列化进每条配置的 JSON。</remarks>
    /// <param name="credentialId">共享凭据的 Id;不是共享凭据引用时为 <see cref="Guid.Empty" />。</param>
    /// <returns>是共享凭据引用且 Id 合法时为 true。</returns>
    public bool TryGetSharedId(out Guid credentialId)
    {
        credentialId = Guid.Empty;
        return ProviderId == SharedProviderId && Guid.TryParse(ItemId, out credentialId);
    }
}

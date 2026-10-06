namespace VelaShell.Infrastructure.Import;

/// <summary>把 <c>ssh_config</c> 里的路径(SSH 库已经展开过 <c>~</c> 与 <c>%</c> 记号)落成本机可用的绝对路径。</summary>
internal static class SshPathResolver
{
    /// <summary>当前用户主目录;取不到时为空串。</summary>
    public static string HomeDirectory => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary><c>~/.ssh</c> 目录(不保证存在)。</summary>
    public static string SshDirectory => Path.Combine(HomeDirectory, ".ssh");

    /// <summary>
    /// 正斜杠换成本机分隔符,相对路径按 <paramref name="baseDirectory" />(通常是 <c>~/.ssh</c>)求绝对。
    /// </summary>
    /// <param name="value">SSH 库展开过的路径(<c>SshHostConfig.ExpandIdentityFiles</c>)。</param>
    /// <param name="baseDirectory">相对路径的基准目录。</param>
    /// <returns>绝对路径;<paramref name="value" /> 为空时返回空串。</returns>
    /// <remarks><c>~</c> 与 <c>%d</c> 曾经在这里展开 —— 与库的那一份各管各的,现在交给库。</remarks>
    public static string ToAbsolute(string value, string baseDirectory)
    {
        string path = value?.Trim() ?? string.Empty;
        if (path.Length == 0)
        {
            return string.Empty;
        }

        path = path.Replace('/', Path.DirectorySeparatorChar);
        try
        {
            return Path.IsPathRooted(path) || baseDirectory.Length == 0
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(baseDirectory, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return value!; // 认不出来就原样留着,让用户在连接对话框里自己看见并修正。
        }
    }
}

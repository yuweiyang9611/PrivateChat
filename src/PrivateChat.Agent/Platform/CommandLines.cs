using System.Text;

namespace PrivateChat.Agent.Platform;

// 这个文件不碰 Windows（测试项目把它直接编进去）。

/// <summary>拼命令行：每个参数按 CommandLineToArgvW 的规矩加引号，子进程拿到的 args 和这里给的一模一样（服务起子进程、写服务的命令行用）。</summary>
internal static class CommandLines
{
    /// <summary>"完整路径" 参数……，按 CommandLineToArgvW 的规矩加引号（子进程的 args 和这里给的一模一样）。</summary>
    public static string Build(string exePath, IReadOnlyList<string> args)
    {
        var text = new StringBuilder(Quote(exePath));
        foreach (var arg in args) text.Append(' ').Append(Quote(arg));
        return text.ToString();
    }

    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return arg;
        var text = new StringBuilder("\"");
        for (var i = 0; ; i++)
        {
            var backslashes = 0;
            while (i < arg.Length && arg[i] == '\\')
            {
                backslashes++;
                i++;
            }
            if (i == arg.Length)
            {
                // 结尾的反斜杠后面紧跟着收尾的引号：要成对
                text.Append('\\', backslashes * 2);
                break;
            }
            if (arg[i] == '"') text.Append('\\', backslashes * 2 + 1).Append('"');
            else text.Append('\\', backslashes).Append(arg[i]);
        }
        return text.Append('"').ToString();
    }
}

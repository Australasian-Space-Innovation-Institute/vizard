// SCARLET demo: command-line player builds, for the targets Unity has no
// command-line flag for (WebGL). Usage:
//   Unity -batchmode -quit -nographics -projectPath <project> -buildTarget WebGL
//         -executeMethod ScarletBuild.BuildWebGL -scarletOut <folder> -logFile <log>
// Builds the scenes enabled in the build settings with the project's player
// settings (WebGL template, compression) into <folder>. A failed build throws,
// so Unity exits non-zero.
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;

public static class ScarletBuild
{
    public static void BuildWebGL() => Build(BuildTarget.WebGL, "");

    public static void BuildLinux() => Build(BuildTarget.StandaloneLinux64, "vizard.x86_64");

    public static void BuildWindows() => Build(BuildTarget.StandaloneWindows64, "vizard.exe");

    private static void Build(BuildTarget target, string playerName)
    {
        string folder = Argument("-scarletOut") ?? throw new ArgumentException("pass -scarletOut <folder>");
        string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            target = target,
            locationPathName = playerName.Length > 0 ? System.IO.Path.Combine(folder, playerName) : folder,
            options = BuildOptions.None,
        };
        BuildReport report = BuildPipeline.BuildPlayer(options);
        UnityEngine.Debug.Log($"ScarletBuild {target}: {report.summary.result}, {report.summary.totalSize} bytes, "
                              + $"{report.summary.totalErrors} errors -> {options.locationPathName}");
        if (report.summary.result != BuildResult.Succeeded)
        {
            throw new Exception($"ScarletBuild {target} failed: {report.summary.result}");
        }
    }

    private static string Argument(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}

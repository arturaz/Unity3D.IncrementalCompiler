﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEditor;
using UnityEditor.Scripting;
using UnityEditor.Scripting.Compilers;
using UnityEditor.Utils;
using UnityEngine;
using Debug = UnityEngine.Debug;

public class CompilationFlags {
    public static bool checkIfBuildCompiles = false;
}

internal class CustomCSharpCompiler : MonoCSharpCompiler {
    public const string COMPILER_DEFINE = "ALWAYS_ON";

    MonoIsland island;

	public CustomCSharpCompiler(MonoIsland island, bool runUpdater) : base(island, runUpdater) {
        this.island = island;
    }

    private string[] GetAdditionalReferences()
	{
		// calling base method via reflection
		var bindingFlags = BindingFlags.Instance | BindingFlags.NonPublic;
		var methodInfo = GetType().BaseType.GetMethod(nameof(GetAdditionalReferences), bindingFlags);
        if (methodInfo == null) return null;
		var result = (string[])methodInfo.Invoke(this, null);
		return result;
    }

    private string GetCompilerPath(List<string> arguments)
	{
		// calling base method via reflection
		var bindingFlags = BindingFlags.Instance | BindingFlags.NonPublic;
		var methodInfo = GetType().BaseType.GetMethod(nameof(GetCompilerPath), bindingFlags);
		var result = (string)methodInfo.Invoke(this, new object[] {arguments});
		return result;
	}

	private string GetUniversalCompilerPath()
	{
		var basePath = Path.Combine(Directory.GetCurrentDirectory(), "Compiler");
		var compilerPath = Path.Combine(basePath, "UniversalCompiler.exe");
		return File.Exists(compilerPath) ? compilerPath : null;
	}

    public bool buildingForEditor() {
        try
        {
            // 2019.1.6+
            return island._buildingForEditor;
        }
        catch (Exception e)
        {
            // previous versions
            return (bool) island.GetType().GetField("_editor").GetValue(island);
        }
    }

	// Copy of MonoCSharpCompiler.StartCompiler()
	// The only reason it exists is to call the new implementation
	// of GetCompilerPath(...) which is non-virtual unfortunately.
	protected override Program StartCompiler()
	{
		var arguments = new List<string>
		{
			"-debug",
			"-target:library",
			"-nowarn:0169",
			"-out:" + PrepareFileName(island._output),
			"-unsafe"
		};

	    arguments.Add("-define:" + COMPILER_DEFINE);

        var unity5References = GetAdditionalReferences();
        if (unity5References != null)
        {
            foreach (string path in unity5References)
            {
                var text = Path.Combine(GetProfileDirectoryViaReflection(), path);
                if (File.Exists(text))
                {
                    arguments.Add("-r:" + PrepareFileName(text));
                }
            }
        }


		foreach (var define in island._defines.Distinct())
		{
			arguments.Add("-define:" + define);
		}

		foreach (var file in island._files)
		{
			arguments.Add(PrepareFileName(file));
		}

        foreach (string fileName in island._references)
        {
            arguments.Add("-r:" + PrepareFileName(fileName));
        }

        if (Application.unityVersion.StartsWith("5."))
        {
            arguments.Add("-custom-option-mdb");
        }

        if (!island._development_player && (!buildingForEditor() || !EditorPrefs.GetBool("AllowAttachedDebuggingOfEditor", true)))
        {
            arguments.Add("-optimize+");
        }
        else
        {
            arguments.Add("-optimize-");
        }

        var universalCompilerPath = GetUniversalCompilerPath();
		if (universalCompilerPath != null)
		{
			// use universal compiler.
			arguments.Add("-define:__UNITY_PROCESSID__" + System.Diagnostics.Process.GetCurrentProcess().Id);

			// this function should be run because it adds an item to arguments
			//var compilerPath = GetCompilerPath(arguments);

			var rspFileName = "Assets/mcs.rsp";
			if (File.Exists(rspFileName))
			{
				arguments.Add("@" + rspFileName);
			}
			//else
			//{
			//	var defaultCompilerName = Path.GetFileNameWithoutExtension(compilerPath);
			//	rspFileName = "Assets/" + defaultCompilerName + ".rsp";
			//	if (File.Exists(rspFileName))
			//		arguments.Add("@" + rspFileName);
			//}


            // Replaced following line to use newer mono runtime for compiler
		    // var program = StartCompiler(island._target, universalCompilerPath, arguments);
            // Side effect: API updater is disabled

            // Disabled API updater
            Program StartCompilerLocal(BuildTarget target, string compiler, List<string> arguments_) {
                AddCustomResponseFileIfPresent(arguments, Path.GetFileNameWithoutExtension(compiler) + ".rsp");
                var responseFile = CommandLineFormatter.GenerateResponseFile(arguments_);

                var p = new Program(CreateOSDependentStartInfo(
                    isWindows: Application.platform == RuntimePlatform.WindowsEditor,
                    processPath: compiler,
                    processArguments: " @" + responseFile,
                    unityEditorDataDir: MonoInstallationFinder.GetFrameWorksFolder()
                ));
                p.Start();
                return p;
            }

            var program = StartCompilerLocal(island._target, universalCompilerPath, arguments);

		    if (!CompilationFlags.checkIfBuildCompiles) return program;

            var compiledDllName = island._output.Split('/').Last();
            if (compiledDllName != "Assembly-CSharp.dll") return program;

		    program.WaitForExit();
            if (program.ExitCode != 0) return program;

		    // message contents are used in CI script, so this shouldnt be changed
		    Debug.Log("Scripts successfully compile in Build mode");
		    // CI script expects to find log from above if process was killed
		    // sometimes process.Kill() happens faster than Debug.Log() logs our message
		    // sleeping the thread ensures that message was logged before we kill the process
		    Thread.Sleep(5000);

		    Process.GetCurrentProcess().Kill();
		    throw new Exception("unreachable code");
		}
		else
		{
			// fallback to the default compiler.
			Debug.LogWarning($"Universal C# compiler not found in project directory. Use the default compiler");
			return base.StartCompiler();
		}
    }

    static ProcessStartInfo CreateOSDependentStartInfo(
        bool isWindows, string processPath, string processArguments, string unityEditorDataDir
    ) {
        ProcessStartInfo startInfo;

        if (isWindows)
        {
            startInfo = new ProcessStartInfo(processPath, processArguments);
        }
        else
        {
            string runtimePath;

            if (File.Exists("/Library/Frameworks/Mono.framework/Commands/mono"))
            {
                runtimePath = "/Library/Frameworks/Mono.framework/Commands/mono";
            }
            else if (File.Exists("/usr/local/bin/mono"))
            {
                runtimePath = "/usr/local/bin/mono";
            }
            else
            {
                runtimePath = Path.Combine(unityEditorDataDir, "MonoBleedingEdge/bin/mono");
            }
            startInfo = new ProcessStartInfo(runtimePath, $"{CommandLineFormatter.PrepareFileName(processPath)} {processArguments}");
        }

        var vars = startInfo.EnvironmentVariables;
        vars.Add("UNITY_DATA", unityEditorDataDir);

        startInfo.CreateNoWindow = true;
        startInfo.WorkingDirectory = Application.dataPath + "/..";
        startInfo.RedirectStandardError = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.UseShellExecute = false;

        return startInfo;
    }

    new static void AddCustomResponseFileIfPresent(List<string> arguments, string responseFileName)
    {
        var path = Path.Combine("Assets", responseFileName);
        if (!File.Exists(path))
            return;
        arguments.Add("@" + path);
    }

	// In Unity 5.5 and earlier GetProfileDirectory() was an instance method of MonoScriptCompilerBase class.
	// In Unity 5.6 the method is removed and the profile directory is detected differently.
	private string GetProfileDirectoryViaReflection()
	{
		var monoScriptCompilerBaseType = typeof(MonoScriptCompilerBase);
		var getProfileDirectoryMethodInfo = monoScriptCompilerBaseType.GetMethod("GetProfileDirectory", BindingFlags.NonPublic | BindingFlags.Instance);
		if (getProfileDirectoryMethodInfo != null)
		{
			// For any Unity version prior to 5.6
			string result = (string)getProfileDirectoryMethodInfo.Invoke(this, null);
			return result;
		}

		// For Unity 5.6
		var monoIslandType = typeof(MonoIsland);
		var apiCompatibilityLevelFieldInfo = monoIslandType.GetField("_api_compatibility_level");
		var apiCompatibilityLevel = (ApiCompatibilityLevel)apiCompatibilityLevelFieldInfo.GetValue(island);

		string profile;
		if (apiCompatibilityLevel != ApiCompatibilityLevel.NET_2_0)
		{
			profile = GetMonoProfileLibDirectory(apiCompatibilityLevel);
		}
		else
		{
			profile = "2.0-api";
		}

		string profileDirectory = GetProfileDirectory(profile, "MonoBleedingEdge");
		return profileDirectory;
	}

	private static string GetMonoProfileLibDirectory(ApiCompatibilityLevel apiCompatibilityLevel)
	{
		var buildPipelineType = typeof(BuildPipeline);
		var compatibilityProfileToClassLibFolderMethodInfo = buildPipelineType.GetMethod("CompatibilityProfileToClassLibFolder", BindingFlags.NonPublic | BindingFlags.Static);
		string profile = (string)compatibilityProfileToClassLibFolderMethodInfo.Invoke(null, new object[] { apiCompatibilityLevel });

		var apiCompatibilityLevelNet46 = (ApiCompatibilityLevel)3;
		string monoInstallation = apiCompatibilityLevel != apiCompatibilityLevelNet46 ? "Mono" : "MonoBleedingEdge";
		return GetProfileDirectory(profile, monoInstallation);
	}

	private static string GetProfileDirectory(string profile, string monoInstallation)
	{
		string monoInstallation2 = MonoInstallationFinder.GetMonoInstallation(monoInstallation);
		return Path.Combine(monoInstallation2, Path.Combine("lib", Path.Combine("mono", profile)));
	}
}

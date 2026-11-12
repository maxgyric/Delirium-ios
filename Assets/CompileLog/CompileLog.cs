// The compiler's errors into compile-log.txt beside the project, where they can be read without the Editor's
// Console. Its own assembly (no references): it keeps working while the game's scripts don't compile, once it has
// been compiled and loaded the first time.
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

[InitializeOnLoad]
static class CompileLog
{
    static readonly string Path_ = Path.Combine(Path.GetDirectoryName(Application.dataPath), "compile-log.txt");

    static CompileLog()
    {
        CompilationPipeline.compilationStarted += _ =>
        {
            try { File.WriteAllText(Path_, "compiling " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n"); } catch (Exception) { }
        };
        CompilationPipeline.assemblyCompilationFinished += (assembly, messages) =>
        {
            var lines = new StringBuilder();
            foreach (var m in messages)
                if (m.type == CompilerMessageType.Error) lines.AppendLine(Path.GetFileName(assembly) + ": " + m.message);
            if (lines.Length == 0) return;
            try { File.AppendAllText(Path_, lines.ToString()); } catch (Exception) { }
        };
        CompilationPipeline.compilationFinished += _ =>
        {
            try { File.AppendAllText(Path_, "done " + DateTime.Now.ToString("HH:mm:ss") + "\n"); } catch (Exception) { }
        };
    }
}

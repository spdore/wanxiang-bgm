using System;
using System.IO;
using System.Reflection;

internal static class EncryptedAudioImportSmoke
{
    private static int Main(string[] args)
    {
        if (args.Length != 4)
        {
            Console.Error.WriteLine("usage: smoke.exe <player-exe> <encrypted-source> <keyless-source> <task-directory>");
            return 2;
        }

        try
        {
            Assembly player = Assembly.LoadFrom(args[0]);
            Type operationType = player.GetType("BgmHotkey.SongImportOperation", true);
            Type converterType = player.GetType("BgmHotkey.EncryptedAudioConverter", true);
            Type validatorType = player.GetType("BgmHotkey.AudioImportValidator", true);
            object operation = Activator.CreateInstance(operationType, true);

            MethodInfo inspect = validatorType.GetMethod("Inspect", BindingFlags.Public | BindingFlags.Static);
            object inspected = inspect.Invoke(null, new object[] { args[1], args[3], 1, operation });
            string inspectStatus = Convert.ToString(inspected.GetType().GetField("Status").GetValue(inspected));
            bool encrypted = (bool)inspected.GetType().GetField("IsEncrypted").GetValue(inspected);
            long sourceLength = (long)inspected.GetType().GetField("Length").GetValue(inspected);
            if (inspectStatus != "Inspected" || !encrypted || sourceLength <= 0)
                throw new InvalidOperationException("Isolated format inspection failed: " + inspectStatus);
            Console.WriteLine("Isolated file inspection and bounded header detection: OK ({0} bytes)", sourceLength);

            MethodInfo convertDetected = converterType.GetMethod("ConvertDetected", BindingFlags.Public | BindingFlags.Static);
            object missingKey = convertDetected.Invoke(null, new object[] { args[2], args[3], 2, new FileInfo(args[2]).Length, operation });
            string missingStatus = Convert.ToString(missingKey.GetType().GetField("Status").GetValue(missingKey));
            if (missingStatus != "KeyUnavailable")
                throw new InvalidOperationException("Keyless QMCv2 did not return KeyUnavailable: " + missingStatus);
            Console.WriteLine("Keyless QMCv2 fails explicitly, then the same operation continues: OK");

            object conversion = convertDetected.Invoke(null, new object[] { args[1], args[3], 1, sourceLength, operation });
            Type conversionType = conversion.GetType();
            string status = Convert.ToString(conversionType.GetField("Status").GetValue(conversion));
            string output = Convert.ToString(conversionType.GetField("OutputPath").GetValue(conversion));
            string container = Convert.ToString(conversionType.GetField("Container").GetValue(conversion));
            string message = Convert.ToString(conversionType.GetField("Message").GetValue(conversion));
            if (status != "Converted" || container != "wav" || !File.Exists(output))
                throw new InvalidOperationException("Host conversion failed with status " + status + ": " + message);

            MethodInfo validate = validatorType.GetMethod("Validate", BindingFlags.Public | BindingFlags.Static);
            long outputLength = new FileInfo(output).Length;
            object validation = validate.Invoke(null, new object[] { output, outputLength, true, args[3], 1, operation });
            string validationStatus = Convert.ToString(validation.GetType().GetField("Status").GetValue(validation));
            double duration = Convert.ToDouble(validation.GetType().GetField("DurationSeconds").GetValue(validation));
            if (validationStatus != "Valid" || duration < 1.9)
                throw new InvalidOperationException("Isolated Media Foundation validation failed: " + validationStatus);
            Console.WriteLine("Host conversion and isolated full-decode validation: OK ({0}, {1:F3} seconds)", container.ToUpperInvariant(), duration);

            string playerRoot = Path.GetDirectoryName(Path.GetFullPath(args[0]));
            string libraryPath = Path.Combine(playerRoot, "bgm");
            Directory.CreateDirectory(libraryPath);
            string duplicate = Path.Combine(libraryPath, "Already Imported.wav");
            File.Copy(output, duplicate, false);
            MethodInfo reuse = validatorType.GetMethod("FindReusableAudioPath", BindingFlags.Public | BindingFlags.Static);
            object reuseResult = reuse.Invoke(null, new object[] { output, outputLength, args[3], 1, operation });
            string reuseStatus = Convert.ToString(reuseResult.GetType().GetField("Status").GetValue(reuseResult));
            string reusablePath = Convert.ToString(reuseResult.GetType().GetField("Path").GetValue(reuseResult));
            if (reuseStatus != "Found" || !String.Equals(Path.GetFullPath(reusablePath), Path.GetFullPath(duplicate), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Isolated same-content reuse failed: " + reuseStatus);
            Console.WriteLine("Isolated content deduplication: OK");

            File.Delete(duplicate);
            MethodInfo prepare = validatorType.GetMethod("PrepareLibraryCopy", BindingFlags.Public | BindingFlags.Static);
            object prepared = prepare.Invoke(null, new object[] { output, outputLength, 1, operation });
            string preparedStatus = Convert.ToString(prepared.GetType().GetField("Status").GetValue(prepared));
            string preparedPath = Convert.ToString(prepared.GetType().GetField("PreparedPath").GetValue(prepared));
            if (preparedStatus != "Prepared" || !File.Exists(preparedPath) || new FileInfo(preparedPath).Length != outputLength)
                throw new InvalidOperationException("Isolated bounded library copy failed: " + preparedStatus);

            Type settingsType = player.GetType("BgmHotkey.AppSettings", true);
            object settings = Activator.CreateInstance(settingsType);
            settingsType.GetMethod("Normalize", BindingFlags.Public | BindingFlags.Instance).Invoke(settings, null);
            Type libraryType = player.GetType("BgmHotkey.SongLibrary", true);
            object track = libraryType.GetMethod("AddPrepared", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { settings, preparedPath, "Synthesized QMC.wav" });
            string storedName = Convert.ToString(track.GetType().GetProperty("FileName").GetValue(track, null));
            if (storedName != "Synthesized QMC.wav") throw new InvalidOperationException("Prepared file was not atomically registered in the disposable library.");
            object[] saveArguments = new object[] { settings, "" };
            bool saved = (bool)player.GetType("BgmHotkey.SettingsStore", true).GetMethod("TrySave", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, saveArguments);
            if (!saved) throw new InvalidOperationException("Disposable TEST_BUILD settings did not save: " + Convert.ToString(saveArguments[1]));
            if (!File.Exists(args[1]) || !File.Exists(args[2])) throw new InvalidOperationException("The source files were modified or removed.");
            Console.WriteLine("Bounded copy, atomic library registration, and isolated settings save: OK");
            return 0;
        }
        catch (TargetInvocationException ex)
        {
            Console.Error.WriteLine("Encrypted import smoke failed: {0}", ex.InnerException == null ? ex.Message : ex.InnerException.Message);
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Encrypted import smoke failed: {0}", ex.Message);
            return 1;
        }
    }
}

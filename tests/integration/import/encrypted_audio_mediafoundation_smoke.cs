using System;
using System.Reflection;

internal static class EncryptedAudioMediaFoundationSmoke
{
    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("usage: smoke.exe <player-exe> <audio-file>");
            return 2;
        }

        try
        {
            Assembly player = Assembly.LoadFrom(args[0]);
            Type catalog = player.GetType("BgmHotkey.AudioFormatCatalog", true);
            MethodInfo validate = catalog.GetMethod("ValidateCompleteAudio", BindingFlags.Public | BindingFlags.Static);
            if (validate == null) throw new MissingMethodException("ValidateCompleteAudio");
            Func<bool> neverCancel = delegate { return false; };
            object duration = validate.Invoke(null, new object[] { args[1], neverCancel });
            Console.WriteLine("Media Foundation full-decode validation: OK ({0:F3} seconds)", duration);
            return 0;
        }
        catch (TargetInvocationException ex)
        {
            Console.Error.WriteLine("Media Foundation full-decode validation failed: {0}", ex.InnerException == null ? ex.Message : ex.InnerException.Message);
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Media Foundation smoke failed: {0}", ex.Message);
            return 1;
        }
    }
}

using System;

// Values recovered from QualitySettings.SetQuality and CardRenderer.
// This does not identify the quality selected in the user's video.
public static class SourceRenderQuality
{
    public enum Level { High = 0, Middle = 1, Low = 2 }
    public static Level SelectedLevel()
    {
        var args = Environment.GetCommandLineArgs();
        for (int i=0;i<args.Length;i++) {
            if (args[i] != "--quality") continue;
            if (i+1>=args.Length || !Enum.TryParse(args[i+1],true,out Level level) || !Enum.IsDefined(typeof(Level),level))
                throw new ArgumentException("--quality expects High, Middle or Low");
            return level;
        }
        return Level.High;
    }
    public static bool NativeRenderingRequested()
    {
        var args = Environment.GetCommandLineArgs();
        if(Array.IndexOf(args,"--export-dynamic-ui")>=0 || Array.IndexOf(args,"--serve-dynamic-ui")>=0) return true;
        if(Array.IndexOf(args,"--verify-native-mssr")>=0 || Array.IndexOf(args,"--verify-native-kira-puyo")>=0 || Array.IndexOf(args,"--verify-native-circular-kira")>=0) return true;
        return Array.IndexOf(args,"--native-rendergraph")>=0 || Array.IndexOf(args,"--verify-rendergraph")>=0 || Array.IndexOf(args,"--verify-native-interactive")>=0 || Array.IndexOf(args,"--verify-native-immersive")>=0 || Array.IndexOf(args,"--verify-native-touch")>=0 || Array.IndexOf(args,"--verify-native-mrr")>=0 || Array.IndexOf(args,"--verify-native-msr")>=0 || Array.IndexOf(args,"--verify-native-marble")>=0 || Array.IndexOf(args,"--verify-native-ancient")>=0 || Array.IndexOf(args,"--verify-native-future")>=0;
    }
    public static bool DynamicUiExportRequested(out string folder)
    {
        var args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args,"--export-dynamic-ui");
        folder = index >= 0 && index+1 < args.Length ? args[index+1] : null;
        return folder != null;
    }
    public static bool DynamicUiServiceRequested(out string folder)
    {
        var args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args,"--serve-dynamic-ui");
        folder = index >= 0 && index+1 < args.Length ? args[index+1] : null;
        return folder != null;
    }
    public static int DynamicTextDimension(int baseSize, Level level)
    {
        // Native CreateDynamicUIRenderTexture truncates the float product.
        return checked((int)(baseSize*TextFactor(level)));
    }
    public static float CardFactor(Level level)
    {
        switch (level) {
            case Level.High: return 1f;
            case Level.Middle: return .8f;
            case Level.Low: return .7f;
            default: throw new ArgumentOutOfRangeException("level");
        }
    }
    public static float TextFactor(Level level)
    {
        switch (level) {
            case Level.High: return 2f;
            case Level.Middle: case Level.Low: return 1f;
            default: throw new ArgumentOutOfRangeException("level");
        }
    }
    public static int CardTargetSize(Level level)
    {
        float verticalPercentage = (.88f * .5f) / .6026955842971802f;
        float size = (1024f / verticalPercentage) * CardFactor(level);
        return checked((int)Math.Round((double)size, MidpointRounding.ToEven));
    }
}

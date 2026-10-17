namespace AotAnywhere.AppleTools;

/// <summary>An unsupported or malformed tool invocation. The message is shown to the user as-is.</summary>
public class AppleToolException(string message) : Exception(message);

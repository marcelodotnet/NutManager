using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using NutManager.Core.Agent;

namespace NutManager.Infrastructure.AgentConfiguration;

/// <summary>
/// Reads and writes the agent's <c>agent.json</c>.
///
/// This file decides whether a privileged service opens a network listener, so the write is a
/// transaction rather than a <c>File.WriteAllText</c>. The sequence is: validate, write a temporary
/// file beside the target, flush it to the disk, apply the restrictive ACL to that temporary file,
/// read it back and parse it, and only then replace the real file in one move. Every failure before
/// the final step leaves the previous configuration exactly as it was.
///
/// The ACL goes on the temporary file rather than being repaired afterwards. A move carries the
/// source's ACL with it, so setting it first means the file is never — not even briefly — present at
/// its real path with permissions wider than intended: SYSTEM and Administrators, inheritance off,
/// and nobody else.
///
/// Nothing here writes a secret, because the document has nowhere to put one.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAgentConfigurationStore : IAgentConfigurationStore
{
    private const string DirectoryName = "NutManager";
    private const string AgentDirectoryName = "Agent";
    private const string FileName = "agent.json";

    private readonly string _path;

    public WindowsAgentConfigurationStore()
        : this(DefaultPath)
    {
    }

    /// <summary>The path is injectable so the write pipeline can be proven in a temporary directory.</summary>
    public WindowsAgentConfigurationStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>
    /// Under ProgramData, because the configuration belongs to the machine rather than to whoever
    /// installed it. The same location the agent reads.
    /// </summary>
    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        DirectoryName,
        AgentDirectoryName,
        FileName);

    public string Path => _path;

    public bool Exists => File.Exists(_path);

    /// <summary>
    /// The current document, or the legacy default when there is no file.
    ///
    /// An unreadable or malformed file also yields the default, and that default is the named pipe
    /// alone — the same answer the agent itself reaches, so the screen shows what the service would
    /// actually do rather than what the broken file appears to say.
    /// </summary>
    public AgentTransportConfigurationDocument Read()
    {
        try
        {
            if (!File.Exists(_path)) return new AgentTransportConfigurationDocument();

            var parsed = JsonSerializer.Deserialize<AgentTransportConfigurationDocument>(
                File.ReadAllText(_path, Encoding.UTF8),
                AgentTransportConfigurationDocument.SerializerOptions);

            return parsed ?? new AgentTransportConfigurationDocument();
        }
        catch (Exception)
        {
            return new AgentTransportConfigurationDocument();
        }
    }

    public AgentConfigurationWriteResult Write(AgentTransportConfigurationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        // Refused before a byte is written. A document the agent would reject is not one to put on
        // disk: the service would then fail to start because of a file this utility produced.
        if (!AgentTransportConfigurationDocument.Validate(document, out var invalid))
        {
            return AgentConfigurationWriteResult.Failed(invalid ?? "The configuration is not valid.");
        }

        var directory = System.IO.Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return AgentConfigurationWriteResult.Failed($"'{_path}' has no directory to write into.");
        }

        // Beside the target, deliberately. A temporary file on another volume cannot be moved
        // atomically, and the fallback for that is a copy — which is the truncation risk this whole
        // routine exists to avoid.
        var temporary = System.IO.Path.Combine(directory, $"{FileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(document, AgentTransportConfigurationDocument.SerializerOptions);

            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                // Through the writer's buffer, then the stream's, then the operating system's. A
                // machine that loses power here must find either the old file or the new one.
                stream.Flush(flushToDisk: true);
            }

            ApplyRestrictiveAcl(temporary);

            // Read back and parse before committing. A file that serialized cleanly but cannot be
            // deserialized is one the agent will refuse at startup, and this is the last moment at
            // which the previous configuration is still intact.
            var verification = JsonSerializer.Deserialize<AgentTransportConfigurationDocument>(
                File.ReadAllText(temporary, Encoding.UTF8),
                AgentTransportConfigurationDocument.SerializerOptions);

            if (verification is null)
            {
                return AgentConfigurationWriteResult.Failed("The configuration written to disk could not be read back.");
            }

            if (!AgentTransportConfigurationDocument.Validate(verification, out var verifyFailure))
            {
                return AgentConfigurationWriteResult.Failed(
                    verifyFailure ?? "The configuration written to disk did not validate when read back.");
            }

            // One move. On NTFS within a volume this replaces the target atomically, so no reader
            // ever observes a partial file.
            File.Move(temporary, _path, overwrite: true);

            return AgentConfigurationWriteResult.Success;
        }
        catch (UnauthorizedAccessException)
        {
            return AgentConfigurationWriteResult.Failed(
                $"'{_path}' could not be written: access was denied. Run NutManager Agent Config as an administrator.");
        }
        catch (Exception exception)
        {
            return AgentConfigurationWriteResult.Failed($"'{_path}' could not be written ({exception.GetType().Name}).");
        }
        finally
        {
            // A temporary file still present means the write did not commit. Removing it is the only
            // cleanup needed, because the real file was never touched.
            TryDelete(temporary);
        }
    }

    /// <summary>
    /// SYSTEM and Administrators, and nothing inherited.
    ///
    /// The agent runs as LocalSystem and has to read it; an administrator has to change it. Any other
    /// account with write access here could switch a listener on, so inheritance is turned off rather
    /// than merely added to — a permissive ACL further up ProgramData would otherwise flow straight
    /// through into this file.
    /// </summary>
    private static void ApplyRestrictiveAcl(string path)
    {
        var file = new FileInfo(path);
        var security = new FileSecurity();

        // Protected, with inherited rules discarded: the second argument is what makes this a
        // replacement of the inherited set rather than an addition to it.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, AccessControlType.Allow));

        file.SetAccessControl(security);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // A leftover .tmp is untidy, not dangerous: the agent reads agent.json and nothing else.
        }
    }
}

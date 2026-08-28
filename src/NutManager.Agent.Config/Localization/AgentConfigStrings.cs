using System.Globalization;
using NutManager.Core.Models;

namespace NutManager.Agent.Config.Localization;

/// <summary>
/// The utility's own strings, in the two cultures the product ships.
///
/// Deliberately not the desktop application's localizer. That one is bound to a resource set inside
/// NutManager.App's assembly and carries the better part of a thousand strings for pages this window
/// does not have; borrowing it would mean either an assembly reference to the desktop application or a
/// copy of its whole catalogue. Sixty strings in a dictionary is the proportionate answer, and a test
/// keeps the two cultures in step.
///
/// Not translated, on purpose: NutManagerAgent, NutManager Operators, SMB, HTTPS, HTTP.sys, and
/// anything else that is a Windows identifier rather than prose. Translating the name of a service or
/// a group would break the very lookup that names it.
/// </summary>
public sealed class AgentConfigStrings
{
    private static readonly IReadOnlyDictionary<string, string> PtBr = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Window.Title"] = "NutManager Agent Config",
        ["Header.Subtitle"] = "Configure os protocolos e o acesso do NutManager Agent.",
        ["Header.Diagnostics"] = "Diagnóstico",
        ["Header.Configuration"] = "Configuração",

        ["Transport.Title"] = "Transporte",
        ["Transport.Description"] = "Selecione os protocolos que o Agent irá usar.",
        ["Transport.NamedPipe"] = "SMB (Named Pipe)",
        ["Transport.NamedPipe.Description"] = "Comunicação local e segura via Named Pipe do Windows.",
        ["Transport.Https"] = "HTTPS",
        ["Transport.Https.Description"] = "Comunicação remota segura via HTTPS.",
        ["Transport.Active"] = "Ativo",
        ["Transport.Inactive"] = "Inativo",
        ["Transport.LastOne"] = "Pelo menos um transporte deve permanecer ativo. Desmarque um para desativá-lo.",
        ["Transport.Reach.Local"] = "Transporte local: o Named Pipe nunca sai desta máquina.",
        ["Transport.Reach.Network"] = "Transporte de rede: ativar HTTPS abre uma porta neste servidor.",

        ["Https.Title"] = "HTTPS",
        ["Https.Disable"] = "Desativar HTTPS",
        ["Https.Host"] = "Host (FQDN)",
        ["Https.Port"] = "Porta",
        ["Https.Endpoint"] = "Endpoint",
        ["Https.Certificate"] = "Certificado",
        ["Https.Certificate.None"] = "Nenhum certificado selecionado",
        ["Https.Certificate.Empty"] = "Nenhum certificado em LocalMachine\\My.",
        ["Https.Certificate.Issuer"] = "Emitido por",
        ["Https.Certificate.ValidUntil"] = "Válido até",
        ["Https.Thumbprint"] = "Thumbprint",
        ["Https.Certificate.Valid"] = "Certificado válido e compatível com o host.",
        ["Https.Disabled.Hint"] = "Ative HTTPS para configurar host, porta e certificado.",
        ["Https.Copy"] = "Copiar",
        ["Https.Invalid.Host"] = "Informe um host ou FQDN explícito.",
        ["Https.Invalid.HostFormat"] = "O host deve ser um nome simples, sem esquema, porta ou caminho.",
        ["Https.Invalid.Wildcard"] = "O host deve ser um nome explícito, nunca um curinga.",
        ["Https.Invalid.Port"] = "A porta deve estar entre 1 e 65535.",
        ["Https.Cert.Unusable"] = "O certificado não pode ser usado para este endpoint.",
        ["Https.Cert.NoPrivateKey"] = "O certificado não tem chave privada nesta máquina.",
        ["Https.Cert.Expired"] = "O certificado expirou em {0}.",
        ["Https.Cert.NotYetValid"] = "O certificado só é válido a partir de {0}.",
        ["Https.Cert.NoServerAuth"] = "O certificado não permite autenticação de servidor.",
        ["Https.Cert.HostMismatch"] = "O certificado não contempla \"{0}\" no titular nem nos nomes alternativos.",
        ["Https.Certificate.View"] = "Visualizar",
        ["Https.Certificate.Hide"] = "Ocultar",
        ["Https.Import"] = "Importar...",
        ["Https.Import.DialogTitle"] = "Importar certificado",
        ["Https.Import.FileType"] = "Certificados (.pfx, .p12, .cer, .crt)",
        ["Https.Import.PasswordTitle"] = "Senha do certificado",
        ["Https.Import.PasswordPrompt"] = "Informe a senha que protege este arquivo PFX/P12.",
        ["Https.Import.Password"] = "Senha",
        ["Https.Import.Success"] = "Certificado importado e selecionado.",
        ["Https.Import.SuccessWithIssue"] = "Certificado importado. {0}",
        ["Https.Import.PasswordIncorrect"] = "Senha incorreta.",
        ["Https.Import.Unsupported"] = "Use um arquivo .pfx, .p12, .cer ou .crt.",
        ["Https.Import.InvalidFile"] = "Arquivo de certificado inválido.",
        ["Https.Import.Failed"] = "Não foi possível importar o certificado em LocalMachine\\My.",
        ["Https.Certificate.Details"] = "Detalhes do certificado",
        ["Https.Certificate.Subject"] = "Titular",
        ["Https.Certificate.NotBefore"] = "Válido a partir de",
        ["Https.Certificate.NotAfter"] = "Válido até",
        ["Https.Certificate.Sans"] = "Nomes alternativos",
        ["Https.Certificate.NoSans"] = "Nenhum nome alternativo.",
        ["Https.Certificate.PrivateKey"] = "Chave privada",
        ["Https.Certificate.ServerAuth"] = "Autenticação de servidor",
        ["Https.Certificate.HostMatch"] = "Correspondência do host",
        ["Https.Certificate.Match"] = "Compatível",
        ["Https.Certificate.Mismatch"] = "Não corresponde",
        ["Https.Certificate.Yes"] = "Presente",
        ["Https.Certificate.No"] = "Ausente",

        ["Cleanup.Title"] = "Desativar HTTPS",
        ["Cleanup.Message"] = "O transporte HTTPS será desativado. Deseja também remover os recursos de sistema configurados pelo NutManager Agent?",
        ["Cleanup.Firewall"] = "Regra do Windows Firewall",
        ["Cleanup.SslBinding"] = "SSL binding do HTTP.sys",
        ["Cleanup.UrlReservation"] = "URL reservation do HTTP.sys",
        ["Cleanup.CertificateNever"] = "O certificado nunca é removido.",
        ["Cleanup.RemoveAndDisable"] = "Desativar e remover",
        ["Cleanup.DisableOnly"] = "Somente desativar",

        ["Resources.Title"] = "Status da configuração",
        ["Resources.SslBinding"] = "HTTP.sys (SSL Binding)",
        ["Resources.UrlReservation"] = "URL Reservation",
        ["Resources.Firewall"] = "Firewall",
        ["Resources.Firewall.Port"] = "Firewall (TCP {0})",
        ["Resources.Listener"] = "Listener HTTPS",
        ["Resources.Listener.Listening"] = "Ouvindo em {0}",
        ["Resources.Listener.ServiceStopped"] = "Serviço parado; nada está ouvindo.",
        ["Resources.Listener.ServiceMissing"] = "NutManagerAgent não está instalado.",
        ["Resources.Listener.Incomplete"] = "Configuração de HTTPS incompleta.",
        ["Resources.Foreign"] = "Não pertence ao NutManager",
        ["Resources.Absent"] = "Não configurado",
        ["Resources.Unknown"] = "Não foi possível verificar",

        ["Operators.Title"] = "NutManager Operators",
        ["Operators.Description"] = "Este grupo controla quem pode usar o NutManager Agent. Somente seus membros podem executar operações administrativas.",
        ["Operators.AddUser"] = "Adicionar usuário",
        ["Operators.Add"] = "Adicionar",
        ["Operators.Placeholder"] = "DOMINIO\\usuario",
        ["Operators.Missing"] = "NutManager Operators não encontrado",
        ["Operators.Create"] = "Criar grupo",
        ["Operators.Members"] = "Membros",
        ["Operators.NoMembers"] = "Nenhum membro.",
        ["Operators.Added"] = "{0} foi adicionado ao grupo.",
        ["Operators.AlreadyMember"] = "{0} já é membro do grupo.",
        ["Operators.Created"] = "Grupo criado.",
        ["Operators.DirectoryTitle"] = "Criar grupo no domínio",
        ["Operators.DirectoryWarning"] = "Este servidor é um controlador de domínio e não possui uma base local independente. O grupo será criado no diretório do domínio e ficará visível em todos os servidores. Deseja continuar?",
        ["Operators.DirectoryConfirm"] = "Criar no domínio",

        ["Service.Title"] = "Serviço:",
        ["Service.Name"] = "NutManagerAgent",
        ["Service.Start"] = "Iniciar serviço",
        ["Service.Stop"] = "Parar",
        ["Service.Restart"] = "Reiniciar serviço",
        ["Service.State.NotInstalled"] = "Não instalado",
        ["Service.State.Stopped"] = "Parado",
        ["Service.State.Running"] = "Em execução",
        ["Service.State.StartPending"] = "Iniciando",
        ["Service.State.StopPending"] = "Parando",
        ["Service.State.Paused"] = "Pausado",
        ["Service.State.Unknown"] = "Desconhecido",
        ["Service.StartMode"] = "Inicialização: {0}",
        ["Service.RestartRequired"] = "As alterações só terão efeito após reiniciar o NutManagerAgent.",
        ["Service.RestartTitle"] = "Reiniciar o NutManagerAgent",
        ["Service.RestartQuestion"] = "A configuração foi salva. O serviço está em execução e precisa ser reiniciado para aplicar as alterações. Deseja reiniciar agora?",
        ["Service.StoppedAfterApply"] = "Configuração salva. O serviço está parado e usará a nova configuração quando for iniciado.",

        ["Diagnostics.Title"] = "Diagnóstico",
        ["Diagnostics.DotNet"] = ".NET Runtime 10 x64",
        ["Diagnostics.AspNetCore"] = "ASP.NET Core Runtime 10 x64",
        ["Diagnostics.AgentRegistered"] = "NutManagerAgent registrado",
        ["Diagnostics.Nut"] = "NUT detectado",
        ["Diagnostics.Operators"] = "NutManager Operators",
        ["Diagnostics.EventLog"] = "Origem do log de eventos",
        ["Diagnostics.NamedPipe"] = "Named Pipe",
        ["Diagnostics.Https"] = "HTTPS",
        ["Diagnostics.Certificate"] = "Certificado",
        ["Diagnostics.SslBinding"] = "HTTP.sys binding",
        ["Diagnostics.Firewall"] = "Firewall",
        ["Diagnostics.Enabled"] = "Ativo",
        ["Diagnostics.Disabled"] = "Desativado",
        ["Diagnostics.NotInstalled"] = "Não instalado",
        ["Diagnostics.Present"] = "Presente",
        ["Diagnostics.Missing"] = "Ausente",
        ["Diagnostics.NotDetected"] = "Não detectado",

        ["Action.Apply"] = "Aplicar",
        ["Action.Cancel"] = "Cancelar",
        ["Action.Close"] = "Fechar",
        ["Action.Confirm"] = "Confirmar",
        ["Action.Refresh"] = "Atualizar",
        ["Action.ViewLogs"] = "Ver logs",

        ["Status.Ready"] = "Pronto",
        ["Status.Attention"] = "Atenção",
        ["Status.NotConfigured"] = "Não configurado",
        ["Status.Error"] = "Erro",

        ["Message.Saved"] = "Configuração salva.",
        ["Message.Discarded"] = "Alterações descartadas.",
        ["Message.NoChanges"] = "Nenhuma alteração pendente.",
        ["Message.RefreshFailed"] = "Não foi possível ler a configuração desta máquina.",
    };

    private static readonly IReadOnlyDictionary<string, string> EnUs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Window.Title"] = "NutManager Agent Config",
        ["Header.Subtitle"] = "Configure the NutManager Agent's protocols and access.",
        ["Header.Diagnostics"] = "Diagnostics",
        ["Header.Configuration"] = "Configuration",

        ["Transport.Title"] = "Transport",
        ["Transport.Description"] = "Choose the protocols the Agent will use.",
        ["Transport.NamedPipe"] = "SMB (Named Pipe)",
        ["Transport.NamedPipe.Description"] = "Local, secure communication over a Windows named pipe.",
        ["Transport.Https"] = "HTTPS",
        ["Transport.Https.Description"] = "Secure remote communication over HTTPS.",
        ["Transport.Active"] = "Active",
        ["Transport.Inactive"] = "Inactive",
        ["Transport.LastOne"] = "At least one transport must stay enabled. Clear one to disable it.",
        ["Transport.Reach.Local"] = "Local transport: the named pipe never leaves this machine.",
        ["Transport.Reach.Network"] = "Network transport: enabling HTTPS opens a port on this server.",

        ["Https.Title"] = "HTTPS",
        ["Https.Disable"] = "Disable HTTPS",
        ["Https.Host"] = "Host (FQDN)",
        ["Https.Port"] = "Port",
        ["Https.Endpoint"] = "Endpoint",
        ["Https.Certificate"] = "Certificate",
        ["Https.Certificate.None"] = "No certificate selected",
        ["Https.Certificate.Empty"] = "No certificate in LocalMachine\\My.",
        ["Https.Certificate.Issuer"] = "Issued by",
        ["Https.Certificate.ValidUntil"] = "Valid until",
        ["Https.Thumbprint"] = "Thumbprint",
        ["Https.Certificate.Valid"] = "Certificate is valid and matches the host.",
        ["Https.Disabled.Hint"] = "Enable HTTPS to configure the host, port and certificate.",
        ["Https.Copy"] = "Copy",
        ["Https.Invalid.Host"] = "Enter an explicit host or FQDN.",
        ["Https.Invalid.HostFormat"] = "The host must be a bare name, without a scheme, port or path.",
        ["Https.Invalid.Wildcard"] = "The host must be an explicit name, never a wildcard.",
        ["Https.Invalid.Port"] = "The port must be between 1 and 65535.",
        ["Https.Cert.Unusable"] = "The certificate cannot be used for this endpoint.",
        ["Https.Cert.NoPrivateKey"] = "The certificate has no private key on this machine.",
        ["Https.Cert.Expired"] = "The certificate expired on {0}.",
        ["Https.Cert.NotYetValid"] = "The certificate is not valid until {0}.",
        ["Https.Cert.NoServerAuth"] = "The certificate is not marked for server authentication.",
        ["Https.Cert.HostMismatch"] = "The certificate does not name \"{0}\" in its subject or subject alternative names.",
        ["Https.Certificate.View"] = "View",
        ["Https.Certificate.Hide"] = "Hide",
        ["Https.Import"] = "Import...",
        ["Https.Import.DialogTitle"] = "Import certificate",
        ["Https.Import.FileType"] = "Certificates (.pfx, .p12, .cer, .crt)",
        ["Https.Import.PasswordTitle"] = "Certificate password",
        ["Https.Import.PasswordPrompt"] = "Enter the password that protects this PFX/P12 file.",
        ["Https.Import.Password"] = "Password",
        ["Https.Import.Success"] = "Certificate imported and selected.",
        ["Https.Import.SuccessWithIssue"] = "Certificate imported. {0}",
        ["Https.Import.PasswordIncorrect"] = "Incorrect password.",
        ["Https.Import.Unsupported"] = "Use a .pfx, .p12, .cer or .crt file.",
        ["Https.Import.InvalidFile"] = "Invalid certificate file.",
        ["Https.Import.Failed"] = "The certificate could not be imported into LocalMachine\\My.",
        ["Https.Certificate.Details"] = "Certificate details",
        ["Https.Certificate.Subject"] = "Subject",
        ["Https.Certificate.NotBefore"] = "Not before",
        ["Https.Certificate.NotAfter"] = "Not after",
        ["Https.Certificate.Sans"] = "Alternative names",
        ["Https.Certificate.NoSans"] = "No alternative names.",
        ["Https.Certificate.PrivateKey"] = "Private key",
        ["Https.Certificate.ServerAuth"] = "Server authentication",
        ["Https.Certificate.HostMatch"] = "Host match",
        ["Https.Certificate.Match"] = "Matches",
        ["Https.Certificate.Mismatch"] = "Does not match",
        ["Https.Certificate.Yes"] = "Present",
        ["Https.Certificate.No"] = "Missing",

        ["Cleanup.Title"] = "Disable HTTPS",
        ["Cleanup.Message"] = "The HTTPS transport will be disabled. Should the system resources configured by NutManager Agent be removed as well?",
        ["Cleanup.Firewall"] = "Windows Firewall rule",
        ["Cleanup.SslBinding"] = "HTTP.sys SSL binding",
        ["Cleanup.UrlReservation"] = "HTTP.sys URL reservation",
        ["Cleanup.CertificateNever"] = "The certificate is never removed.",
        ["Cleanup.RemoveAndDisable"] = "Disable and remove",
        ["Cleanup.DisableOnly"] = "Disable only",

        ["Resources.Title"] = "Configuration status",
        ["Resources.SslBinding"] = "HTTP.sys (SSL binding)",
        ["Resources.UrlReservation"] = "URL reservation",
        ["Resources.Firewall"] = "Firewall",
        ["Resources.Firewall.Port"] = "Firewall (TCP {0})",
        ["Resources.Listener"] = "HTTPS listener",
        ["Resources.Listener.Listening"] = "Listening on {0}",
        ["Resources.Listener.ServiceStopped"] = "Service stopped; nothing is listening.",
        ["Resources.Listener.ServiceMissing"] = "NutManagerAgent is not installed.",
        ["Resources.Listener.Incomplete"] = "HTTPS configuration is incomplete.",
        ["Resources.Foreign"] = "Not owned by NutManager",
        ["Resources.Absent"] = "Not configured",
        ["Resources.Unknown"] = "Could not be verified",

        ["Operators.Title"] = "NutManager Operators",
        ["Operators.Description"] = "This group controls who may use the NutManager Agent. Only its members can perform administrative operations.",
        ["Operators.AddUser"] = "Add user",
        ["Operators.Add"] = "Add",
        ["Operators.Placeholder"] = "DOMAIN\\user",
        ["Operators.Missing"] = "NutManager Operators was not found",
        ["Operators.Create"] = "Create group",
        ["Operators.Members"] = "Members",
        ["Operators.NoMembers"] = "No members.",
        ["Operators.Added"] = "{0} was added to the group.",
        ["Operators.AlreadyMember"] = "{0} is already a member of the group.",
        ["Operators.Created"] = "Group created.",
        ["Operators.DirectoryTitle"] = "Create the group in the domain",
        ["Operators.DirectoryWarning"] = "This server is a domain controller and has no independent local database. The group will be created in the domain directory and will be visible on every server. Continue?",
        ["Operators.DirectoryConfirm"] = "Create in the domain",

        ["Service.Title"] = "Service:",
        ["Service.Name"] = "NutManagerAgent",
        ["Service.Start"] = "Start service",
        ["Service.Stop"] = "Stop",
        ["Service.Restart"] = "Restart service",
        ["Service.State.NotInstalled"] = "Not installed",
        ["Service.State.Stopped"] = "Stopped",
        ["Service.State.Running"] = "Running",
        ["Service.State.StartPending"] = "Starting",
        ["Service.State.StopPending"] = "Stopping",
        ["Service.State.Paused"] = "Paused",
        ["Service.State.Unknown"] = "Unknown",
        ["Service.StartMode"] = "Startup: {0}",
        ["Service.RestartRequired"] = "The changes take effect only after NutManagerAgent is restarted.",
        ["Service.RestartTitle"] = "Restart NutManagerAgent",
        ["Service.RestartQuestion"] = "The configuration has been saved. The service is running and needs to be restarted for the changes to take effect. Restart it now?",
        ["Service.StoppedAfterApply"] = "Configuration saved. The service is stopped and will use the new configuration when it is started.",

        ["Diagnostics.Title"] = "Diagnostics",
        ["Diagnostics.DotNet"] = ".NET Runtime 10 x64",
        ["Diagnostics.AspNetCore"] = "ASP.NET Core Runtime 10 x64",
        ["Diagnostics.AgentRegistered"] = "NutManagerAgent registered",
        ["Diagnostics.Nut"] = "NUT detected",
        ["Diagnostics.Operators"] = "NutManager Operators",
        ["Diagnostics.EventLog"] = "Event Log source",
        ["Diagnostics.NamedPipe"] = "Named pipe",
        ["Diagnostics.Https"] = "HTTPS",
        ["Diagnostics.Certificate"] = "Certificate",
        ["Diagnostics.SslBinding"] = "HTTP.sys binding",
        ["Diagnostics.Firewall"] = "Firewall",
        ["Diagnostics.Enabled"] = "Enabled",
        ["Diagnostics.Disabled"] = "Disabled",
        ["Diagnostics.NotInstalled"] = "Not installed",
        ["Diagnostics.Present"] = "Present",
        ["Diagnostics.Missing"] = "Missing",
        ["Diagnostics.NotDetected"] = "Not detected",

        ["Action.Apply"] = "Apply",
        ["Action.Cancel"] = "Cancel",
        ["Action.Close"] = "Close",
        ["Action.Confirm"] = "Confirm",
        ["Action.Refresh"] = "Refresh",
        ["Action.ViewLogs"] = "View logs",

        ["Status.Ready"] = "Ready",
        ["Status.Attention"] = "Attention",
        ["Status.NotConfigured"] = "Not configured",
        ["Status.Error"] = "Error",

        ["Message.Saved"] = "Configuration saved.",
        ["Message.Discarded"] = "Changes discarded.",
        ["Message.NoChanges"] = "No pending changes.",
        ["Message.RefreshFailed"] = "This machine's configuration could not be read.",
    };

    private readonly IReadOnlyDictionary<string, string> _strings;

    public AgentConfigStrings(UiLanguagePreference language)
    {
        Language = language;
        _strings = language == UiLanguagePreference.EnUs ? EnUs : PtBr;
    }

    public UiLanguagePreference Language { get; }

    /// <summary>
    /// The culture Windows is running in, which is what an administrator on a server expects. There is
    /// no language switch in this window: it is open for a few minutes, and a preference nobody asked
    /// for would be one more thing to persist and get wrong.
    /// </summary>
    public static UiLanguagePreference DetectLanguage()
    {
        try
        {
            var culture = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            return string.Equals(culture, "pt", StringComparison.OrdinalIgnoreCase)
                ? UiLanguagePreference.PtBr
                : UiLanguagePreference.EnUs;
        }
        catch (Exception)
        {
            return UiLanguagePreference.PtBr;
        }
    }

    public string this[string key] => Get(key);

    /// <summary>
    /// A missing key falls back to pt-BR, the source culture, and then to the key itself. Returning
    /// the key rather than throwing means a gap shows up on screen as an obvious placeholder instead
    /// of taking the window down.
    /// </summary>
    public string Get(string key)
    {
        if (_strings.TryGetValue(key, out var value)) return value;
        return PtBr.TryGetValue(key, out var fallback) ? fallback : key;
    }

    public string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), arguments);

    /// <summary>The keys each culture carries, so a parity test can compare the two sets.</summary>
    public static IReadOnlySet<string> KeysFor(UiLanguagePreference language) =>
        (language == UiLanguagePreference.EnUs ? EnUs : PtBr).Keys.ToHashSet(StringComparer.Ordinal);
}

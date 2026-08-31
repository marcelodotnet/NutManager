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
        ["Https.Certificate.ViewTooltip"] = "Visualizar certificado",
        ["Https.Certificate.Hide"] = "Ocultar",
        ["Https.Select"] = "Selecionar...",
        ["Https.Select.Tooltip"] = "Selecionar certificado instalado",
        ["Https.Select.Title"] = "Selecionar certificado instalado",
        ["Https.Select.Subtitle"] = "Certificados presentes em LocalMachine\\My neste computador.",
        ["Https.Select.Empty"] = "Nenhum certificado instalado foi encontrado.",
        ["Https.Select.Confirm"] = "Selecionar",
        ["Https.Select.Usable"] = "Compatível com este endpoint",
        ["Https.Select.NoPrivateKey"] = "Sem chave privada",
        ["Https.Select.Expired"] = "Fora do período de validade",
        ["Https.Select.NoServerAuth"] = "Sem autenticação de servidor",
        ["Https.Select.HostMismatch"] = "Não corresponde ao host",
        ["Https.Select.Valid"] = "Dentro da validade",
        ["Https.Select.Details"] = "Detalhes",
        ["Https.Select.ValidFrom"] = "Válido a partir de",

        ["Apply.Disabled.NoChanges"] = "Nenhuma alteração pendente.",
        ["Apply.Disabled.NoTransport"] = "Pelo menos um transporte deve permanecer ativo.",
        ["Apply.Disabled.InvalidHost"] = "Informe um host ou FQDN válido para HTTPS.",
        ["Apply.Disabled.InvalidPort"] = "Informe uma porta entre 1 e 65535.",
        ["Apply.Disabled.NoCertificate"] = "Selecione um certificado válido para habilitar HTTPS.",
        ["Apply.Disabled.Busy"] = "Aguarde a operação em andamento terminar.",

        // What Apply reports after an attempt. Deliberately separate from the reasons above: one
        // explains why the button cannot be pressed, the other what happened when it was.
        ["Apply.Result.Saved"] = "Configuração salva.",
        ["Apply.Result.SslBindingConflict"] = "A porta {0} já possui um certificado SSL vinculado.",
        ["Apply.Result.UrlReservationConflict"] = "A URL reservation desta porta pertence a outro aplicativo.",
        ["Apply.Result.HttpsFailed"] = "Não foi possível aplicar a configuração HTTPS.",
        ["Apply.Result.ConfigurationFailed"] = "Não foi possível gravar a configuração do Agent.",

        ["Resources.State.NotChecked"] = "Não verificado",
        ["Resources.NotChecked.Detail"] =
            "Os recursos do Windows só são consultados depois que host, porta e certificado formam um endpoint válido.",

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
        ["Https.Import.AccessDenied"] = "Acesso ao repositório LocalMachine\\My negado. Execute como administrador.",
        ["Https.Import.Unavailable"] = "A importação de certificados não está disponível nesta instalação.",

        ["Https.Reset"] = "Resetar",
        ["Https.Reset.Tooltip"] = "Resetar configuração HTTPS",
        ["Https.Reset.Title"] = "Resetar configuração HTTPS?",
        ["Https.Reset.Message"] =
            "Esta ação removerá as configurações HTTPS criadas pelo NutManager Agent neste computador."
            + "\n\nSerão removidos, quando pertencentes ao NutManager:"
            + "\n  • SSL Binding do HTTP.sys"
            + "\n  • URL Reservation do HTTP.sys"
            + "\n  • Regra do Windows Firewall"
            + "\n  • Configuração HTTPS do Agent"
            + "\n\nO certificado instalado NÃO será removido.",
        ["Https.Reset.Confirm"] = "Resetar HTTPS",
        ["Https.Reset.Done"] = "Configuração HTTPS resetada.",
        ["Https.Reset.Failed"] = "Não foi possível resetar a configuração HTTPS.",
        ["Https.Reset.PartiallyRemoved"] = "Já removido: {0}.",
        ["Https.Reset.LastTransport"] =
            "Ative o SMB (Named Pipe) antes de resetar o HTTPS. Pelo menos um transporte deve permanecer ativo.",

        ["Language.Label"] = "Idioma",
        ["Language.Portuguese"] = "Português (Brasil)",
        ["Language.English"] = "English (United States)",
        ["Https.Certificate.Readonly"] = "Selecionado por Importar...; use o botão ao lado para visualizar.",
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

        // The short state each status column shows. Deliberately not the adapter's own detail: that
        // is written in English by infrastructure, names an AppId or a rule, and runs to several
        // lines inside a quarter-width column. It moves to the tooltip; this is what the card says.
        //
        // Configured and NotConfigured come in two forms because Portuguese agrees with the noun:
        // "SSL Binding configurado" but "URL Reservation configurada". English has no such
        // agreement and maps both forms to the same word.
        ["Resources.State.Configured"] = "Configurado",
        ["Resources.State.ConfiguredFeminine"] = "Configurada",
        ["Resources.State.NotConfigured"] = "Não configurado",
        ["Resources.State.NotConfiguredFeminine"] = "Não configurada",
        ["Resources.State.Foreign"] = "Pertence a outro aplicativo",
        ["Resources.State.UnmanagedRule"] = "Regra existente não gerenciada",
        ["Resources.State.Unknown"] = "Propriedade não confirmada",
        ["Resources.State.Error"] = "Erro na configuração",
        ["Resources.State.HttpsDisabled"] = "HTTPS desativado",
        ["Resources.State.Listener.Active"] = "Ativo",
        ["Resources.State.Listener.Incomplete"] = "Configuração incompleta",
        ["Resources.State.Listener.Unavailable"] = "Listener indisponível",

        // The tooltip. Everything the card no longer shows inline, on one hover.
        ["Resources.Tooltip.State"] = "Estado: {0}",
        ["Resources.Tooltip.Port"] = "Porta: {0}",

        ["Theme.EnableDark"] = "Ativar modo escuro",
        ["Theme.EnableLight"] = "Ativar modo claro",

        ["Service.RestartPending"] = "Reinicialização necessária",
        ["Service.RestartPending.Detail"] =
            "As alterações serão aplicadas ao listener após reiniciar o NutManagerAgent.",

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
        ["Service.Stop"] = "Parar serviço",
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
        ["Https.Certificate.ViewTooltip"] = "View certificate",
        ["Https.Certificate.Hide"] = "Hide",
        ["Https.Select"] = "Select...",
        ["Https.Select.Tooltip"] = "Select installed certificate",
        ["Https.Select.Title"] = "Select installed certificate",
        ["Https.Select.Subtitle"] = "Certificates present in LocalMachine\\My on this computer.",
        ["Https.Select.Empty"] = "No installed certificates were found.",
        ["Https.Select.Confirm"] = "Select",
        ["Https.Select.Usable"] = "Compatible with this endpoint",
        ["Https.Select.NoPrivateKey"] = "No private key",
        ["Https.Select.Expired"] = "Outside its validity period",
        ["Https.Select.NoServerAuth"] = "No server authentication",
        ["Https.Select.HostMismatch"] = "Does not match the host",
        ["Https.Select.Valid"] = "Within its validity period",
        ["Https.Select.Details"] = "Details",
        ["Https.Select.ValidFrom"] = "Valid from",

        ["Apply.Disabled.NoChanges"] = "No pending changes.",
        ["Apply.Disabled.NoTransport"] = "At least one transport must stay enabled.",
        ["Apply.Disabled.InvalidHost"] = "Enter a valid host or FQDN for HTTPS.",
        ["Apply.Disabled.InvalidPort"] = "Enter a port between 1 and 65535.",
        ["Apply.Disabled.NoCertificate"] = "Select a valid certificate to enable HTTPS.",
        ["Apply.Disabled.Busy"] = "Wait for the operation in progress to finish.",

        ["Apply.Result.Saved"] = "Configuration saved.",
        ["Apply.Result.SslBindingConflict"] = "Port {0} already has an SSL certificate binding.",
        ["Apply.Result.UrlReservationConflict"] = "The URL reservation for this port belongs to another application.",
        ["Apply.Result.HttpsFailed"] = "The HTTPS configuration could not be applied.",
        ["Apply.Result.ConfigurationFailed"] = "The Agent configuration could not be written.",

        ["Resources.State.NotChecked"] = "Not checked",
        ["Resources.NotChecked.Detail"] =
            "Windows resources are only queried once the host, port and certificate form a valid endpoint.",

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
        ["Https.Import.AccessDenied"] = "Access to the LocalMachine\\My store was denied. Run as administrator.",
        ["Https.Import.Unavailable"] = "Certificate import is not available in this installation.",

        ["Https.Reset"] = "Reset",
        ["Https.Reset.Tooltip"] = "Reset HTTPS configuration",
        ["Https.Reset.Title"] = "Reset the HTTPS configuration?",
        ["Https.Reset.Message"] =
            "This will remove the HTTPS configuration created by the NutManager Agent on this computer."
            + "\n\nThe following are removed when they belong to NutManager:"
            + "\n  • The HTTP.sys SSL binding"
            + "\n  • The HTTP.sys URL reservation"
            + "\n  • The Windows Firewall rule"
            + "\n  • The Agent's HTTPS configuration"
            + "\n\nThe installed certificate will NOT be removed.",
        ["Https.Reset.Confirm"] = "Reset HTTPS",
        ["Https.Reset.Done"] = "HTTPS configuration reset.",
        ["Https.Reset.Failed"] = "The HTTPS configuration could not be reset.",
        ["Https.Reset.PartiallyRemoved"] = "Already removed: {0}.",
        ["Https.Reset.LastTransport"] =
            "Enable SMB (Named Pipe) before resetting HTTPS. At least one transport must stay active.",

        ["Language.Label"] = "Language",
        ["Language.Portuguese"] = "Português (Brasil)",
        ["Language.English"] = "English (United States)",
        ["Https.Certificate.Readonly"] = "Chosen with Import...; use the button beside it to view.",
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

        ["Resources.State.Configured"] = "Configured",
        ["Resources.State.ConfiguredFeminine"] = "Configured",
        ["Resources.State.NotConfigured"] = "Not configured",
        ["Resources.State.NotConfiguredFeminine"] = "Not configured",
        ["Resources.State.Foreign"] = "Owned by another application",
        ["Resources.State.UnmanagedRule"] = "Existing unmanaged rule",
        ["Resources.State.Unknown"] = "Ownership not confirmed",
        ["Resources.State.Error"] = "Configuration error",
        ["Resources.State.HttpsDisabled"] = "HTTPS disabled",
        ["Resources.State.Listener.Active"] = "Active",
        ["Resources.State.Listener.Incomplete"] = "Incomplete configuration",
        ["Resources.State.Listener.Unavailable"] = "Listener unavailable",

        ["Resources.Tooltip.State"] = "State: {0}",
        ["Resources.Tooltip.Port"] = "Port: {0}",

        ["Theme.EnableDark"] = "Enable dark mode",
        ["Theme.EnableLight"] = "Enable light mode",

        ["Service.RestartPending"] = "Restart required",
        ["Service.RestartPending.Detail"] =
            "The changes will reach the listener once NutManagerAgent is restarted.",

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
        ["Service.Stop"] = "Stop service",
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
    /// The culture Windows is running in, which is what an administrator on a server expects on first
    /// use. It is the last of three answers: an explicit choice wins, then a saved preference, then
    /// this. Portuguese is matched on the language rather than the region, so pt-PT and pt-BR both
    /// land on Portuguese; every other culture takes en-US.
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

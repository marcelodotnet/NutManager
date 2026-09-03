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
        ["Header.Home"] = "Início",
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
        ["Toast.EndpointCopied"] = "Copiado!",
        ["Toast.EndpointCopyFailed"] = "Não foi possível copiar.",
        ["Https.Invalid.Host"] = "Informe um host ou FQDN explícito.",
        ["Https.Invalid.HostFormat"] = "O host deve ser um nome simples, sem esquema, porta ou caminho.",
        ["Https.Invalid.Wildcard"] = "O host deve ser um nome explícito, nunca um curinga.",
        ["Https.Invalid.Port"] = "A porta deve estar entre 1 e 65535.",
        ["Https.Cert.Unusable"] = "O certificado não pode ser usado para este endpoint.",
        ["Https.Cert.NoPrivateKey"] = "O certificado não tem chave privada nesta máquina.",
        ["Https.Cert.Expired"] = "O certificado expirou em {0}.",
        ["Https.Cert.NotYetValid"] = "O certificado só é válido a partir de {0}.",
        ["Https.Cert.NoServerAuth"] = "O certificado não permite autenticação de servidor.",
        ["Https.Cert.HostMismatch"] = "O certificado não corresponde ao host informado.",
        ["Https.Cert.HostMismatch.Detail"] = "Host informado: {0}. O certificado contempla: {1}.",
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

        ["Https.Reset"] = "Resetar HTTPS",
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
        ["Resources.Listener.NotAnswering"] = "O endpoint não respondeu; o serviço está em execução, mas nada está ouvindo em {0}.",
        ["Resources.Listener.Checking"] = "Ainda não houve resposta desta janela; a primeira verificação está em andamento.",
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
        ["Resources.State.Listener.Checking"] = "Verificando",

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
        ["Operators.Removed"] = "{0} foi removido do grupo.",
        ["Operators.NotMember"] = "{0} não era membro do grupo.",
        ["Operators.RemoveFailed"] = "Não foi possível remover o usuário.",
        ["Operators.NotFound"] = "O usuário informado não foi encontrado.",
        ["Operators.Failed"] = "Não foi possível adicionar o usuário.",
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
        ["Diagnostics.ServiceConfiguration"] = "Configuração do serviço",
        ["Diagnostics.ServiceConfiguration.Failed"] = "Falha ao consultar",
        ["Diagnostics.ServiceConfiguration.NoDetail"] =
            "O Gerenciador de Controle de Serviços não informou um motivo.",
        ["Diagnostics.ServiceInstall"] = "Instalação do serviço",
        ["Diagnostics.ServiceInstall.Failed"] = "Falha ao registrar",
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

        ["Settings.Title"] = "Configurações",
        ["Settings.Tab.General"] = "Geral",
        ["Settings.Tab.Users"] = "Usuários",
        ["Settings.Tab.Agent"] = "Agent",
        ["Settings.Tab.About"] = "Sobre",

        ["Service.StartType.Automatic"] = "Automático",
        ["Service.StartType.Manual"] = "Manual",
        ["Service.StartType.Disabled"] = "Desativado",
        ["Service.StartType.Boot"] = "Inicialização do sistema",
        ["Service.StartType.System"] = "Sistema",

        ["Settings.Startup.Title"] = "Iniciar NutManager Agent com o Windows",
        ["Settings.Startup.Description"] =
            "Quando ativado, o Windows inicia o serviço automaticamente no boot. " +
            "Desativar não interrompe o serviço em execução; apenas passa a exigir início manual.",
        ["Settings.Startup.NotInstalled"] = "O serviço NutManagerAgent não está instalado nesta máquina.",
        ["Settings.Startup.Automatic.Done"] = "O serviço passará a iniciar automaticamente com o Windows.",
        ["Settings.Startup.Manual.Done"] = "O serviço passará a exigir início manual.",
        ["Settings.Startup.Failed"] = "Não foi possível alterar o modo de início do serviço.",
        ["Settings.Startup.Install"] = "Instalar serviço",
        ["Settings.Startup.Manual.Title"] = "Alterar inicialização do serviço?",
        ["Settings.Startup.Manual.Question"] =
            "O NutManager Agent deixará de iniciar automaticamente com o Windows. " +
            "O serviço em execução não será interrompido.",
        ["Settings.Startup.Manual.Confirm"] = "Alterar para manual",

        ["Settings.Appearance.Section"] = "Aparência e idioma",
        ["Settings.Appearance.Theme"] = "Tema",
        ["Settings.Appearance.Theme.Description"] = "Alterna entre o tema claro e o escuro desta janela.",
        ["Settings.Appearance.Theme.Light"] = "Claro",
        ["Settings.Appearance.Theme.Dark"] = "Escuro",
        ["Settings.Appearance.Language"] = "Idioma",
        ["Settings.Appearance.Language.Description"] = "Idioma da interface desta janela.",

        ["Settings.Agent.Install.Title"] = "Instalação do Agent",
        ["Settings.Agent.Install.Missing"] =
            "O NutManager Agent ainda não está instalado como serviço do Windows.",
        ["Settings.Agent.Install.Working"] = "Instalando...",
        ["Settings.Agent.Install.GroupFailed"] =
            "Não foi possível criar o grupo NutManager Operators, então o serviço não foi instalado.",
        ["Settings.Agent.Install.GroupDirectory"] =
            "O grupo NutManager Operators precisa ser criado no domínio antes de instalar o serviço.",
        ["Settings.Agent.Remove.Action"] = "Remover serviço",
        ["Settings.Agent.Remove.Working"] = "Removendo...",
        ["Settings.Agent.Remove.Title"] = "Remover NutManager Agent?",
        ["Settings.Agent.Remove.Question"] =
            "O serviço NutManager Agent será removido deste computador. " +
            "Se estiver em execução, ele será interrompido para concluir a remoção. " +
            "O grupo NutManager Operators e seus usuários não serão removidos.",
        ["Settings.Agent.Remove.Absent"] = "O serviço já não estava instalado.",
        ["Settings.Agent.Remove.Pending"] =
            "O serviço foi marcado para remoção e ainda está registrado. " +
            "Feche o console de Serviços do Windows e verifique novamente.",
        ["Settings.Agent.Remove.NotOwned"] =
            "Existe um serviço com esse nome que não pertence ao NutManager, então nada foi removido.",
        ["Settings.Agent.Remove.QueryFailed"] =
            "Não foi possível verificar a configuração do serviço, então nada foi removido.",
        ["Settings.Agent.Remove.Failed"] = "Não foi possível remover o serviço.",
        ["Settings.Agent.Install.Already"] =
            "O NutManager Agent já está instalado como serviço do Windows.",
        ["Settings.Agent.Install.Action"] = "Instalar serviço",
        ["Settings.Agent.Install.Failed"] = "Não foi possível instalar o serviço.",
        ["Settings.Agent.Section"] = "Serviço e comunicação",
        ["Settings.Permissions.Group"] = "Grupo do Windows: {0}",
        ["Settings.Permissions.Intro"] =
            "Usuários autorizados a administrar o NutManager Agent.",
        ["Settings.Permissions.Members"] = "Usuários autorizados",
        ["Settings.Permissions.Empty"] = "Nenhum usuário configurado.",
        ["Settings.Permissions.Select"] = "Selecionar usuário...",
        ["Settings.Permissions.GroupMissing"] =
            "O grupo NutManager Operators ainda não existe. Ele é criado ao instalar o Agent.",
        ["Settings.Permissions.Remove"] = "Remover",
        ["Settings.Permissions.Remove.Title"] = "Remover usuário?",
        ["Settings.Permissions.Remove.Question"] =
            "{0} deixará de ter permissão para administrar o NutManager Agent. " +
            "A conta do Windows não será removida.",
        ["Settings.Agent.Service"] = "Serviço",
        ["Settings.Agent.StartMode"] = "Modo de início",
        ["Settings.Agent.Account"] = "Conta",
        ["Settings.Agent.Transports"] = "Transportes",
        ["Settings.Agent.HttpsPort"] = "Porta HTTPS",
        ["Settings.Agent.None"] = "Nenhum",

        ["About.Version"] = "Versão",
        ["About.Build"] = "Build",
        ["About.DotNet"] = ".NET Runtime",
        ["About.AspNetCore"] = "ASP.NET Core Runtime",
        ["About.Developer"] = "Desenvolvedor",
        ["About.ProjectPage"] = "GitHub",
        ["About.ProjectPage.Open"] = "Abrir página do projeto",
        ["About.ProjectPage.Failed"] = "Não foi possível abrir o navegador. Copie o endereço acima.",
        ["About.Product"] = "NutManager Agent",
        ["About.Terms"] = "Termos",
        ["About.Terms.Description"] =
            "Consulte os termos de uso e as informações legais do NutManager.",
        ["About.Terms.View"] = "Ver termos",
        ["About.Unknown"] = "Desconhecido",

        ["Terms.Title"] = "Termos",
        ["Terms.Back"] = "Voltar",
    };

    private static readonly IReadOnlyDictionary<string, string> EnUs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Window.Title"] = "NutManager Agent Config",
        ["Header.Subtitle"] = "Configure the NutManager Agent's protocols and access.",
        ["Header.Diagnostics"] = "Diagnostics",
        ["Header.Home"] = "Home",
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
        ["Toast.EndpointCopied"] = "Copied!",
        ["Toast.EndpointCopyFailed"] = "Could not copy.",
        ["Https.Invalid.Host"] = "Enter an explicit host or FQDN.",
        ["Https.Invalid.HostFormat"] = "The host must be a bare name, without a scheme, port or path.",
        ["Https.Invalid.Wildcard"] = "The host must be an explicit name, never a wildcard.",
        ["Https.Invalid.Port"] = "The port must be between 1 and 65535.",
        ["Https.Cert.Unusable"] = "The certificate cannot be used for this endpoint.",
        ["Https.Cert.NoPrivateKey"] = "The certificate has no private key on this machine.",
        ["Https.Cert.Expired"] = "The certificate expired on {0}.",
        ["Https.Cert.NotYetValid"] = "The certificate is not valid until {0}.",
        ["Https.Cert.NoServerAuth"] = "The certificate is not marked for server authentication.",
        ["Https.Cert.HostMismatch"] = "The certificate does not match the specified host.",
        ["Https.Cert.HostMismatch.Detail"] = "Specified host: {0}. The certificate covers: {1}.",
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

        ["Https.Reset"] = "Reset HTTPS",
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
        ["Resources.Listener.NotAnswering"] = "The endpoint did not answer; the service is running, but nothing is listening on {0}.",
        ["Resources.Listener.Checking"] = "This window has not had an answer yet; the first check is under way.",
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
        ["Resources.State.Listener.Checking"] = "Checking",

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
        ["Operators.Removed"] = "{0} was removed from the group.",
        ["Operators.NotMember"] = "{0} was not a member of the group.",
        ["Operators.RemoveFailed"] = "The user could not be removed.",
        ["Operators.NotFound"] = "The Windows account could not be found.",
        ["Operators.Failed"] = "The user could not be added.",
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
        ["Diagnostics.ServiceConfiguration"] = "Service configuration",
        ["Diagnostics.ServiceConfiguration.Failed"] = "Could not be queried",
        ["Diagnostics.ServiceConfiguration.NoDetail"] =
            "The Service Control Manager gave no reason.",
        ["Diagnostics.ServiceInstall"] = "Service installation",
        ["Diagnostics.ServiceInstall.Failed"] = "Could not be registered",
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

        ["Settings.Title"] = "Settings",
        ["Settings.Tab.General"] = "General",
        ["Settings.Tab.Users"] = "Users",
        ["Settings.Tab.Agent"] = "Agent",
        ["Settings.Tab.About"] = "About",

        ["Service.StartType.Automatic"] = "Automatic",
        ["Service.StartType.Manual"] = "Manual",
        ["Service.StartType.Disabled"] = "Disabled",
        ["Service.StartType.Boot"] = "Boot",
        ["Service.StartType.System"] = "System",

        ["Settings.Startup.Title"] = "Start NutManager Agent with Windows",
        ["Settings.Startup.Description"] =
            "When on, Windows starts the service automatically at boot. " +
            "Turning it off does not stop the running service; it only means the service must be started by hand.",
        ["Settings.Startup.NotInstalled"] = "The NutManagerAgent service is not installed on this machine.",
        ["Settings.Startup.Automatic.Done"] = "The service will now start automatically with Windows.",
        ["Settings.Startup.Manual.Done"] = "The service will now have to be started by hand.",
        ["Settings.Startup.Failed"] = "The service start mode could not be changed.",
        ["Settings.Startup.Install"] = "Install service",
        ["Settings.Startup.Manual.Title"] = "Change how the service starts?",
        ["Settings.Startup.Manual.Question"] =
            "NutManager Agent will no longer start automatically with Windows. " +
            "The running service will not be stopped.",
        ["Settings.Startup.Manual.Confirm"] = "Change to manual",

        ["Settings.Appearance.Section"] = "Appearance and language",
        ["Settings.Appearance.Theme"] = "Theme",
        ["Settings.Appearance.Theme.Description"] = "Switches this window between the light and dark themes.",
        ["Settings.Appearance.Theme.Light"] = "Light",
        ["Settings.Appearance.Theme.Dark"] = "Dark",
        ["Settings.Appearance.Language"] = "Language",
        ["Settings.Appearance.Language.Description"] = "The interface language of this window.",

        ["Settings.Agent.Install.Title"] = "Agent installation",
        ["Settings.Agent.Install.Missing"] =
            "NutManager Agent is not installed as a Windows service yet.",
        ["Settings.Agent.Install.Working"] = "Installing...",
        ["Settings.Agent.Install.GroupFailed"] =
            "The NutManager Operators group could not be created, so the service was not installed.",
        ["Settings.Agent.Install.GroupDirectory"] =
            "The NutManager Operators group has to be created in the directory before the service " +
            "can be installed.",
        ["Settings.Agent.Remove.Action"] = "Remove service",
        ["Settings.Agent.Remove.Working"] = "Removing...",
        ["Settings.Agent.Remove.Title"] = "Remove NutManager Agent?",
        ["Settings.Agent.Remove.Question"] =
            "The NutManager Agent service will be removed from this computer. " +
            "If it is running, it will be stopped to complete the removal. " +
            "The NutManager Operators group and its users will not be removed.",
        ["Settings.Agent.Remove.Absent"] = "The service was already not installed.",
        ["Settings.Agent.Remove.Pending"] =
            "The service is marked for removal and is still registered. " +
            "Close the Windows Services console and check again.",
        ["Settings.Agent.Remove.NotOwned"] =
            "A service with that name exists but does not belong to NutManager, so nothing was removed.",
        ["Settings.Agent.Remove.QueryFailed"] =
            "The service configuration could not be verified, so nothing was removed.",
        ["Settings.Agent.Remove.Failed"] = "The service could not be removed.",
        ["Settings.Agent.Install.Already"] =
            "NutManager Agent is already installed as a Windows service.",
        ["Settings.Agent.Install.Action"] = "Install service",
        ["Settings.Agent.Install.Failed"] = "The service could not be installed.",
        ["Settings.Agent.Section"] = "Service and communication",
        ["Settings.Permissions.Group"] = "Windows group: {0}",
        ["Settings.Permissions.Intro"] =
            "Users authorized to administer NutManager Agent.",
        ["Settings.Permissions.Members"] = "Authorized users",
        ["Settings.Permissions.Empty"] = "No user configured.",
        ["Settings.Permissions.Select"] = "Select user...",
        ["Settings.Permissions.GroupMissing"] =
            "The NutManager Operators group does not exist yet. It is created when the Agent is installed.",
        ["Settings.Permissions.Remove"] = "Remove",
        ["Settings.Permissions.Remove.Title"] = "Remove user?",
        ["Settings.Permissions.Remove.Question"] =
            "{0} will no longer be allowed to administer NutManager Agent. " +
            "The Windows account will not be removed.",
        ["Settings.Agent.Service"] = "Service",
        ["Settings.Agent.StartMode"] = "Start mode",
        ["Settings.Agent.Account"] = "Account",
        ["Settings.Agent.Transports"] = "Transports",
        ["Settings.Agent.HttpsPort"] = "HTTPS port",
        ["Settings.Agent.None"] = "None",

        ["About.Version"] = "Version",
        ["About.Build"] = "Build",
        ["About.DotNet"] = ".NET Runtime",
        ["About.AspNetCore"] = "ASP.NET Core Runtime",
        ["About.Developer"] = "Developer",
        ["About.ProjectPage"] = "GitHub",
        ["About.ProjectPage.Open"] = "Open the project page",
        ["About.ProjectPage.Failed"] = "The browser could not be opened. Copy the address above.",
        ["About.Product"] = "NutManager Agent",
        ["About.Terms"] = "Terms",
        ["About.Terms.Description"] =
            "Read the NutManager terms of use and legal information.",
        ["About.Terms.View"] = "View terms",
        ["About.Unknown"] = "Unknown",

        ["Terms.Title"] = "Terms",
        ["Terms.Back"] = "Back",
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

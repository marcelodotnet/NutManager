# Termos de Uso — NutManager

<!--
  Documento canônico. O pt-BR é a fonte; docs/TERMS-OF-USE.en-US.md é tradução.

  A representação RTF exibida pelos instaladores é gerada a partir deste arquivo por
  scripts/build-terms-rtf.ps1 e versionada em installer/Common/Terms/. Ao editar aqui, regenere os
  RTF. Uma divergência entre o texto aceito no instalador e o texto publicado é pior que um texto
  desatualizado nos dois lugares, porque só um dos dois é o que o usuário efetivamente aceitou.

  PENDENTE PARA v1.0.1: a T41 acrescentará uma verificação informativa de release no GitHub. Isso
  torna falsa, como está escrita, a frase da seção 10 sobre links externos serem acessados somente
  mediante ação explícita do usuário. Estes Termos precisam ser ressincronizados depois da T41 e
  antes de marcar a v1.0.1. Até lá esta versão é a corrente, não a final.
-->

**Última atualização:** 27 de agosto de 2026

## 1. Sobre o NutManager

O **NutManager** é um software opensource desenvolvido para facilitar o monitoramento, configuração, diagnóstico e administração de instalações do **Network UPS Tools (NUT)** em sistemas Windows.

O NutManager é uma ferramenta independente e não faz parte, não é afiliado e não representa oficialmente o projeto Network UPS Tools.

## 2. Aceitação

Ao instalar, executar ou utilizar o NutManager, o usuário declara estar ciente das características, limitações e riscos inerentes à administração de sistemas de energia, nobreaks, serviços Windows e arquivos de configuração do NUT.

Caso não concorde com estas condições, o usuário pode simplesmente deixar de utilizar o software.

## 3. Software livre e licença

O código-fonte do NutManager é disponibilizado sob a **GNU General Public License version 2.0 — GPL v2.0**.

Estes Termos de Uso **não substituem, restringem ou modificam os direitos concedidos pela GPL v2.0** relativos a:

- uso;
- estudo;
- cópia;
- modificação;
- redistribuição;
- distribuição de versões modificadas.

Em caso de conflito entre estes Termos e os direitos concedidos pela GPL v2.0 sobre o código licenciado, prevalecem os termos da licença GPL aplicável.

## 4. Finalidade

O NutManager pode ser utilizado para, entre outras funções:

- monitorar servidores NUT e nobreaks;
- consultar estado, carga, bateria e demais informações disponibilizadas pelo NUT;
- gerenciar perfis de servidores;
- editar configurações do NUT por interface gráfica;
- gerar prévias antes da aplicação de alterações;
- realizar backup, validação, substituição segura e rollback de configurações;
- consultar e administrar o serviço Windows associado ao NUT;
- consultar portas seriais disponíveis;
- utilizar o NutManager Agent para administração remota autorizada;
- realizar diagnósticos relacionados ao ambiente NUT.

As funcionalidades efetivamente disponíveis podem variar conforme versão, ambiente, permissões, hardware, sistema operacional e configuração do NUT.

## 5. Responsabilidade do administrador

O NutManager é uma ferramenta administrativa. Algumas operações podem afetar diretamente a disponibilidade do sistema de monitoramento de energia.

O usuário é responsável por:

- compreender as alterações antes de aplicá-las;
- verificar a prévia apresentada pelo software;
- manter backups adequados;
- garantir que possui autorização para administrar os computadores envolvidos;
- manter credenciais, certificados e chaves de acesso protegidos;
- confirmar que configurações são compatíveis com seu hardware e sua versão do NUT;
- testar alterações em ambiente adequado quando necessário;
- avaliar o impacto antes de parar ou reiniciar serviços.

**A interrupção do serviço NUT pode interromper o monitoramento do nobreak e mecanismos automáticos de desligamento associados a quedas de energia.**

## 6. Alterações de configuração

O NutManager utiliza mecanismos destinados a reduzir o risco de corrupção de arquivos, incluindo:

- validação;
- prévia antes da gravação;
- backup;
- escrita temporária;
- substituição segura;
- verificação posterior;
- rollback quando aplicável.

Esses mecanismos reduzem riscos, mas **não garantem que toda alteração produzirá o comportamento desejado no NUT ou no hardware conectado**.

O usuário continua responsável pelo conteúdo e pelas consequências da configuração aplicada.

O NutManager deliberadamente **não reinicia automaticamente o NUT após uma alteração de configuração**. Quando necessário, essa ação deve ser realizada separadamente pelo administrador.

## 7. Controle de serviços

O NutManager pode permitir operações como:

- iniciar;
- parar;
- reiniciar;

o serviço Windows relacionado ao NUT.

Essas operações podem causar indisponibilidade temporária ou permanente do monitoramento.

O usuário deve avaliar o impacto antes de executá-las.

## 8. NutManager Agent

O **NutManager Agent** é um componente opcional destinado à administração de servidores Windows remotos.

O Agent:

- opera sob as permissões configuradas no servidor;
- utiliza mecanismos de autenticação do Windows;
- restringe operações administrativas a usuários autorizados;
- não funciona como mecanismo genérico de execução remota;
- não deve ser tratado como substituto para as políticas de segurança da organização.

É responsabilidade do administrador configurar corretamente:

- o grupo `NutManager Operators`;
- permissões;
- certificados;
- HTTPS, quando utilizado;
- firewall;
- políticas do Windows;
- credenciais autorizadas.

## 9. Credenciais e segurança

Determinadas funcionalidades podem utilizar credenciais para:

- SFTP;
- SMB;
- NutManager Agent;
- autenticação no NUT.

Quando suportado, o NutManager pode utilizar o **Windows Credential Manager** para armazenamento de credenciais.

O usuário é responsável por:

- proteger sua conta Windows;
- limitar o acesso administrativo à máquina;
- proteger chaves privadas;
- utilizar certificados válidos;
- controlar quem pertence aos grupos autorizados;
- remover credenciais quando deixarem de ser necessárias.

Nenhum mecanismo de armazenamento de credenciais deve ser considerado proteção absoluta contra comprometimento de uma máquina já controlada por um atacante.

## 10. Privacidade e telemetria

A versão atual do NutManager **não possui sistema de telemetria ou coleta automática de dados de uso pelo desenvolvedor**.

As informações processadas pelo aplicativo são utilizadas localmente ou transmitidas diretamente aos servidores configurados pelo próprio usuário, conforme a funcionalidade utilizada.

Isso pode incluir conexões com:

- servidor NUT;
- servidor SFTP;
- compartilhamento SMB;
- NutManager Agent.

Links externos, como GitHub e documentação, somente são acessados mediante ação explícita do usuário.

## 11. Software e serviços de terceiros

O NutManager depende ou interage com tecnologias de terceiros, incluindo o **Network UPS Tools** e componentes do sistema operacional Windows.

Esses projetos possuem seus próprios:

- termos;
- licenças;
- políticas;
- limitações;
- requisitos de suporte.

O desenvolvedor do NutManager não controla alterações realizadas por esses terceiros.

## 12. Compatibilidade

O suporte oficial do NutManager pode variar entre versões.

Uma determinada combinação de:

- Windows;
- NUT;
- nobreak;
- driver;
- adaptador USB/serial;
- configuração de rede;
- domínio Active Directory;
- SMB;
- SFTP;
- certificados;

pode apresentar comportamento diferente de outro ambiente.

A indicação de compatibilidade não representa garantia de funcionamento com todos os equipamentos ou configurações possíveis.

## 13. Ausência de garantia

O NutManager é fornecido **sem garantia de funcionamento ininterrupto, ausência de erros ou adequação a uma finalidade específica**, respeitados os termos da licença aplicável.

Não existe garantia de que:

- todo hardware será reconhecido;
- todo driver NUT funcionará;
- toda configuração será válida para determinado equipamento;
- conexões remotas estarão sempre disponíveis;
- alterações realizadas pelo usuário não causarão indisponibilidade.

## 14. Limitação de responsabilidade

Na máxima extensão permitida pela legislação aplicável, o desenvolvedor e os colaboradores do NutManager não serão responsáveis por danos decorrentes do uso ou da impossibilidade de uso do software, incluindo, entre outros:

- perda de dados;
- perda ou corrupção de configuração;
- indisponibilidade de serviços;
- falha de monitoramento;
- desligamentos inesperados;
- interrupção de sistemas;
- perda de produtividade;
- danos relacionados a configuração incorreta;
- problemas decorrentes de hardware, drivers ou software de terceiros.

O usuário deve manter procedimentos adequados de backup, contingência e recuperação.

## 15. Uso inadequado

O NutManager foi desenvolvido para administração legítima de sistemas sob responsabilidade ou autorização do usuário.

O software não deve ser utilizado para acessar, modificar ou administrar sistemas sem autorização do respectivo responsável.

Esta disposição descreve a finalidade prevista do produto e **não altera os direitos concedidos pela GPL v2.0 sobre o código-fonte**.

## 16. Bugs e limitações conhecidas

Como qualquer software, o NutManager pode possuir defeitos, incompatibilidades ou comportamentos ainda não identificados.

Problemas conhecidos podem ser documentados no:

- Manual do Operador;
- GitHub do projeto;
- notas de versão.

O usuário deve consultar a documentação da versão instalada antes de realizar mudanças relevantes em ambientes de produção.

## 17. Atualizações

O NutManager não garante atualização automática.

É responsabilidade do administrador verificar periodicamente a existência de novas versões, especialmente quando houver:

- correções de segurança;
- correções de bugs;
- mudanças de compatibilidade;
- atualizações do runtime incluído no software.

## 18. Documentação

A documentação procura representar o comportamento real do produto, mas pode existir diferença temporária entre uma versão recente do software e a documentação publicada.

Em caso de dúvida operacional, recomenda-se verificar:

1. versão instalada;
2. notas de versão;
3. documentação correspondente;
4. repositório oficial.

## 19. Alterações destes Termos

Estes Termos podem ser atualizados para refletir alterações no NutManager, sua distribuição ou documentação.

A versão atual deve indicar sua data de revisão.

## 20. Projeto e desenvolvedor

**NutManager**

Projeto desenvolvido e mantido por **Marcelo Pacheco — @marcelodotnet**.

Repositório oficial:

https://github.com/marcelodotnet/NutManager

Licença do código-fonte:

**GNU General Public License v2.0**

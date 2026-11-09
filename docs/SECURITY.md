# Segurança e privacidade

## Princípios

- Local First.
- Captura transparente e controlada pelo usuário.
- Sem upload automático de áudio, transcrição ou prompts.
- Minimização de dados.
- Retenção explícita e exclusão disponível.

## Controles

- Respeitar permissões do sistema operacional e políticas corporativas.
- Indicador visível de captura e botão de parada.
- Proteger configurações sensíveis com mecanismos nativos, como DPAPI no Windows e Keychain no macOS quando aplicável.
- Não armazenar segredos no código ou repositório.
- Validar caminhos, tamanhos e checksums dos modelos.
- Restringir serviços locais a loopback/IPC; não expor workers à rede local por padrão.
- Verificar que o Ollama escuta apenas em loopback (ex.: `OLLAMA_HOST` não definido como `0.0.0.0`) e alertar caso contrário; documentar verificações automáticas de atualização dos runtimes de terceiros.
- Sanitizar logs e dumps.
- Banco SQLite não criptografado no P0 (limitação documentada); criptografia do banco avaliada na V1 (ver `DATABASE.md`).

## Consentimento e conformidade

O usuário deve obter as autorizações necessárias para capturar/transcrever reuniões e respeitar políticas da organização, termos da plataforma e legislação aplicável, incluindo LGPD. O aplicativo não deve ocultar a captura nem contornar mecanismos de consentimento. Ao iniciar cada sessão, o app exibe um aviso de consentimento/responsabilidade (RF-AUD-011).

## Modelo de ameaça inicial

- Acesso local não autorizado ao perfil do usuário.
- Modelo adulterado ou arquivo de download malicioso.
- Worker local ou runtime de LLM (Ollama) exposto indevidamente na rede.
- Voz da Iris ouvida pelos participantes através do microfone quando o usuário usa caixas de som.
- Vazamento de conteúdo por logs/crash dumps.
- Captura acidental de áudio fora da reunião.
- Realimentação do TTS no pipeline de entrada.

## Fora do P0

Sincronização remota, contas, billing e backup em nuvem. Se forem introduzidos, exigem threat model e revisão de privacidade próprios.

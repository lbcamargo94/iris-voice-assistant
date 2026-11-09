# Model Manager e Resource Manager

## Model Manager

Responsabilidades:

- Catálogo de modelos compatíveis.
- Download explícito e retomável.
- Verificação de checksum e tamanho.
- Metadados: nome, versão, formato, idioma, quantização, licença, origem e requisitos.
- Instalar, atualizar, selecionar e remover modelos.
- Impedir carregamento de artefatos não verificados.
- Manter histórico de versão e compatibilidade.

## Manifesto de modelo

Cada modelo deverá ter manifesto versionado com:

- `modelId`
- `version`
- `provider`
- `artifactUri` (somente para download explícito)
- `sha256`
- `licenseId` e localização do texto da licença
- `languages`
- `task`
- `runtimeRequirements`
- `minimumRamGb`
- `recommendedVramGb`
- `quantization`

Não aceitar URLs ou manifests arbitrários de fontes não confiáveis sem validação.

## Resource Manager

Monitorar:

- CPU e uso por worker quando disponível.
- RAM do processo e do sistema.
- VRAM e uso de GPU.
- Modelos carregados.
- Filas de áudio e inferência.
- Latência e pressão de memória.

Políticas:

- Priorizar captura/STT.
- Limitar concorrência.
- Descarregar modelos ociosos quando necessário.
- Evitar OOM por carregamento simultâneo.
- Oferecer perfis `Balanced`, `Performance` e `Low Resource` futuramente.

No P0, começar com medição e limites simples; otimização automática avançada fica para versões posteriores.

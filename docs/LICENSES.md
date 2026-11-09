# Licenças e distribuição de modelos

## Regra

Nenhum modelo, voz, pacote ou runtime será incluído em distribuição comercial antes de revisão da licença específica da versão e do artefato utilizado.

## Inventário obrigatório

Manter uma tabela atualizada com:

- Componente e versão.
- URL oficial e origem do artefato.
- Licença do código.
- Licença dos pesos/modelos.
- Licença da voz/dataset quando aplicável.
- Obrigações de atribuição.
- Restrições comerciais ou de redistribuição.
- SHA-256 do artefato.
- Data e responsável pela revisão.

## Candidatos e pontos de atenção

Licenças abaixo são a informação conhecida no planejamento e **devem ser confirmadas** no artefato e versão efetivamente usados.

| Componente | Ponto de atenção |
| --- | --- |
| faster-whisper / CTranslate2 | Código MIT; verificar licença dos pesos de cada modelo (Whisper, distil-whisper) |
| NVIDIA Parakeet / sherpa-onnx | Pesos e runtime com licenças próprias (CC-BY / Apache esperadas); confirmar |
| Silero VAD | Verificar licença da versão usada |
| Opus-MT (Helsinki-NLP) | Pesos CC-BY 4.0 esperados: exige atribuição |
| Argos Translate e pacote en→pt | Código e pacote com licenças distintas |
| NLLB-200 | **Excluído**: licença não comercial (ADR-010) |
| Ollama e modelo LLM escolhido | Runtime e pesos com licenças distintas; alguns modelos têm restrições de uso |
| Piper e voz pt-BR | O projeto original foi arquivado e o sucessor é distribuído sob GPL-3.0; cada voz tem licença própria. Avaliar impacto comercial (execução como processo separado) com revisão jurídica |
| NVIDIA CUDA/cuDNN (via pip) | Termos de redistribuição da NVIDIA |
| Avalonia UI e bibliotecas .NET | Verificar cada pacote |
| NAudio ou alternativa para captura Windows | Verificar licença |
| AMI Meeting Corpus (testes) | CC-BY 4.0: atribuição; uso apenas como dado de teste |

A licença do runtime não determina automaticamente a licença dos pesos ou dos pacotes de idioma. Verificar cada artefato separadamente.

## Distribuição

- Preferir baixar modelos durante instalação/configuração, não embutir todos no instalador.
- Exibir licença e tamanho antes do download.
- Verificar checksum.
- Preservar avisos e atribuições exigidos.
- Reavaliar licenças em cada atualização.

Este documento é um checklist técnico e não substitui revisão jurídica.

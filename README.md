# Anotador de Tela

Aplicativo para desenhar, escrever e fazer capturas sobre a área de trabalho do Windows. Pause o desenho para voltar a usar os aplicativos abaixo com as anotações ainda visíveis.

[**Baixar a versão mais recente**](https://github.com/Fernando-Almeida582/anotador-de-tela/releases/latest)

## Instalação

1. Baixe o arquivo **Windows-x64.zip** da release e extraia todo o conteúdo em uma pasta.
2. Abra **Anotador.exe**. O aplicativo aparece na bandeja do Windows.
3. Dê dois cliques no ícone da bandeja para começar a desenhar.

Requer Windows x64 e .NET Desktop Runtime 10 x64. Para abrir o aplicativo ao entrar no Windows, clique com o botão direito no ícone da bandeja e escolha **Adicionar à inicialização do Windows**.

## Como usar

| Ação | Controle |
| --- | --- |
| Ativar ou pausar o desenho | Dois cliques no ícone da bandeja ou **Ctrl+Alt+A** |
| Pausar e esconder a barra | O primeiro **Esc** pausa. O segundo esconde a barra enquanto ela estiver em foco. |
| Ocultar ou mostrar as marcas | **Ctrl+Alt+H** |
| Limitar o desenho e a captura a uma região | **Área**, depois arraste dentro de um monitor. Use **Tela inteira** para remover a seleção. |
| Concluir e salvar em PNG | **Concluir e salvar** ou **Ctrl+Alt+S**. A imagem fica em **Imagens → Anotador**. |
| Escolher onde salvar ou copiar para a área de transferência | **Salvar…** ou **Copiar** na barra de ferramentas. |

O menu da bandeja mostra os atalhos registrados pelo Windows, inclusive os alternativos. Pausar, esconder a barra e sair do aplicativo não salvam imagens.

A barra inclui caneta, seta, retângulo, texto, seletor de cor, borracha e desfazer. A borracha muda a cor do item sob o ponteiro antes de apagá-lo. O monitor ativo pode ser escolhido desenhando nele. As marcas e o histórico ficam separados por monitor.

### Gravação de tela

Na barra do Anotador, clique em **Gravar… → Gravar monitor inteiro**. Para gravar um recorte, primeiro use **Área** para arrastar a seleção e depois escolha **Gravar área selecionada**. A gravação mantém o mesmo enquadramento até terminar.

Use as ferramentas de desenho normalmente. As anotações visíveis aparecem no vídeo conforme são feitas. Para voltar a interagir com os aplicativos durante a gravação, clique em **Pausar**. Clique em **Retomar desenho** para continuar anotando.

Clique em **Parar gravação** na barra ou na bandeja para salvar o vídeo em **Vídeos → Anotador**. O atalho **Ctrl+Alt+R** inicia a gravação da área selecionada ou do monitor ativo e também encerra a gravação. Se o Windows usar um atalho alternativo, ele aparece no menu da bandeja.

O vídeo é salvo em AVI (Motion JPEG) a 15 quadros por segundo, sem áudio. A gravação termina automaticamente ao atingir o limite de aproximadamente 2 GB.

### Google Lens

Clique em **Lens** e arraste uma área. Ao soltar, o navegador padrão envia o recorte da tela, sem as anotações, ao Google Lens para pesquisa visual. Pressione **Esc** antes do envio para cancelar.

## Compilação

Instale o SDK do .NET 10 no Windows e, na pasta do código, execute:

```powershell
dotnet build Anotador.csproj -c Release
```

Para gerar a distribuição para Windows x64:

```powershell
dotnet publish Anotador.csproj -c Release -r win-x64 --self-contained false -p:DebugType=None
```

O aplicativo usa C#, WPF e APIs do Windows.

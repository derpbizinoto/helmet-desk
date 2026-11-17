import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from docxlib import *

HERE = os.path.dirname(os.path.abspath(__file__))
SHOTS = os.path.join(HERE, '..', 'aero')
OUT = sys.argv[1]
os.makedirs(os.path.join(HERE, 'img'), exist_ok=True)
_g = [0]


def grad(layers, base, w=360, h=90):
    _g[0] += 1
    return gradient_png(os.path.join(HERE, 'img', f'g{_g[0]}.png'), layers, base, w, h)


V = (0, 0, 0, 1)      # vertical, top -> bottom
H = (0, 0, 1, 0)      # horizontal, left -> right


def stops_text(stops):
    return '  →  '.join(f'{css(c)}' + ('' if len(stops) == 2 and o in (0, 1) else f' {round(o*100)}%') for c, o in stops)


def cover(d, name, subtitle, tagline, objective, pills):
    inner = (d.para(d.run(name, bold=True, color=LIGHT, size=56, spacing=60), after=60, before=120)
             + d.para(d.run(subtitle, color="C8A8F5", size=24), after=80)
             + d.para(d.run(tagline, color="B9A8CB", size=18), after=120))
    d.table([9808], [[d.cell(inner, 9808, fill=DARK, margins=(360, 420))]], border=None)
    d.add(d.para(d.run('OBJETIVO', bold=True, color="8950D9", size=18, spacing=40), before=200, after=60))
    d.p(objective)
    w = 9808 // len(pills)
    widths = [w] * (len(pills) - 1) + [9808 - w * (len(pills) - 1)]
    cells = []
    for (big, small), cw in zip(pills, widths):
        cells.append(d.cell(d.para(d.run(big, bold=True, color=LIGHT, size=20), after=20, align="center")
                            + d.para(d.run(small, color="C8A8F5", size=16), after=0, align="center"), cw, fill=DARK2, margins=(160, 120)))
    d.table(widths, [cells], border="34204D")


def palette(d, rows, base):
    """rows: (name, color, use)"""
    widths = [2300, 1700, 4108, 1700]
    out = []
    for name, c, use in rows:
        a = parse(c)[0]
        hx = css(c) + ('' if a == 255 else f'\n({pct(c)}%)')
        hx = css(c) if a == 255 else f'{css(c)[:7]} · {pct(c)}%'
        out.append([f'**{name}**', hx, use, d.swatch_cell(over(c, base), widths[3])])
    d.data_table(widths, ['Token', 'Valor', 'Uso', 'Amostra'], out)


def gradients(d, rows, base):
    """rows: (name, description, layers)"""
    widths = [2200, 4908, 2700]
    out = []
    for name, desc, layers in rows:
        img = grad(layers, base)
        pic = d.cell(d.para(d.image_xml(img, 1.62, 0.405), after=0, align="center"), widths[2], margins=(80, 80))
        out.append([f'**{name}**', desc, pic])
    d.data_table(widths, ['Degradê', 'Valores (CSS)', 'Amostra'], out)


def dodont(d, do, dont):
    widths = [4904, 4904]
    rows = [[d.cell(d.para(d.run('FAZER', bold=True, color="FFFFFF", size=18), after=0), widths[0], fill="2E8A62"),
             d.cell(d.para(d.run('EVITAR', bold=True, color="FFFFFF", size=18), after=0), widths[1], fill="B8345A")]]
    for a, b in zip(do, dont):
        rows.append([d.cell(d.para(d.rich('•  ' + a, size=18), after=0), widths[0]),
                     d.cell(d.para(d.rich('•  ' + b, size=18), after=0), widths[1])])
    d.table(widths, rows)


def cr(a, b):
    return f'{contrast(a, b):.1f}:1'


# =====================================================================================
# 1) VIOLET TERMINAL 2.0 (Helmet Desk, tema Clássico)
# =====================================================================================
def classic():
    BASE = '#100B18'
    d = Doc('GUIA DE IDENTIDADE VISUAL  /  DESIGN SYSTEM', 'VIOLET TERMINAL 2.0  •  tema Clássico')
    cover(d, 'VIOLET TERMINAL 2.0', 'Identidade visual para interfaces de aplicativos — versão com degradês',
          'Minimalismo  •  Roxo profundo  •  Roboto + Geo  •  Degradês discretos de mesma cor',
          'Evolução do Violet Terminal original, aplicada no tema Clássico do Helmet Desk. Mantém a mesma paleta, '
          'a mesma sobriedade e os mesmos componentes, mas troca as cores 100% sólidas por degradês suaves dentro do '
          'mesmo tom (mais claro em cima, mais escuro embaixo) e um brilho discreto nos botões principais. Serve de '
          'referência para aplicar a mesma identidade em outros aplicativos e extensões.',
          [('DARK UI', 'Modo escuro como padrão'), ('DEGRADÊS SUAVES', 'Mesmo tom, claro → escuro'), ('ROBOTO + GEO', 'Interface + marca')])

    d.h1('1. Conceito e princípios')
    d.p('Violet Terminal é uma identidade de interface minimalista com predominância de roxo escuro. Ela combina a sobriedade '
        'de ferramentas de produtividade com detalhes violeta que orientam a navegação e destacam as ações importantes. '
        'A versão 2.0 acrescenta profundidade com degradês leves, sem perder o caráter plano e limpo.')
    d.h2('Princípios fundamentais')
    for t in ['**Simplicidade funcional:** cada elemento tem uma função clara; nada de decoração sem propósito.',
              '**Hierarquia evidente:** títulos, ação principal, conteúdo e metadados se distinguem de relance.',
              '**Degradês discretos:** só variações de luminosidade do mesmo tom (diferença de 4–8%), sempre do mais claro em cima '
              'para o mais escuro embaixo, como uma luz suave vinda do topo. Nunca misturar matizes diferentes.',
              '**Consistência:** os mesmos tokens, componentes e estados em todos os aplicativos.',
              '**Discrição visual:** o roxo é acento; a interface não vira um bloco saturado.']:
        d.bullet(t)
    d.image(os.path.join(SHOTS, 'degrade', '1-hoje.png'), 6.3, 4.095, 'Referência: Helmet Desk, tela Hoje, tema Clássico (Violet Terminal 2.0).')

    d.h1('2. Paleta de cores')
    d.p('A paleta é a mesma do Violet Terminal original. Os valores devem ser reutilizados como tokens; não crie tons novos sem necessidade. '
        'Os degradês da seção 3 são feitos a partir destes mesmos tons.')
    palette(d, [('Background', '#100B18', 'Fundo geral da aplicação (base dos degradês de fundo)'),
                ('Surface', '#191222', 'Cards, painéis, campos e áreas secundárias'),
                ('Surface elevada', '#221831', 'Hover, modais, menus e superfícies sobrepostas'),
                ('Border', '#34204D', 'Divisores, bordas sutis, item selecionado discreto'),
                ('Primary', '#8950D9', 'Ação principal, seleção e destaque'),
                ('Primary hover', '#7744BC', 'Hover de ações primárias (escurece)'),
                ('Accent', '#C8A8F5', 'Ícones ativos, links, foco e destaques leves'),
                ('Texto principal', '#F5F1FA', 'Títulos e conteúdo principal'),
                ('Texto secundário', '#B9A8CB', 'Descrições, legendas e metadados')], BASE)
    d.h2('Cores semânticas')
    palette(d, [('Sucesso', '#64D6A2', 'Concluído, saldo positivo, confirmação'),
                ('Aviso', '#F2C36B', 'Atenção, prioridade média, vence em breve'),
                ('Erro / perigo', '#FF6F91', 'Erro, exclusão, atrasado, saldo negativo'),
                ('Informação', '#78B7FF', 'Dicas e avisos neutros')], BASE)
    d.p('As cores semânticas aparecem só em feedbacks de status e não competem com o roxo como cor de identidade.')
    d.h2('Tons auxiliares')
    palette(d, [('Hover line', '#4C3270', 'Borda em hover de campos, combos e barra de rolagem'),
                ('Chip', '#3D2B5E', 'Fundo de chips e marcadores pequenos'),
                ('Accent suave', '#33C8A8F5', 'Fundo de ícone em diálogos (Accent a 20%)'),
                ('Perigo suave', '#33FF6F91', 'Fundo de ícone em diálogos de exclusão (Erro a 20%)'),
                ('Atrasado', '#5A2A3E', 'Borda de linha vencida (Finanças)'),
                ('Véu de modal', '#B3100B18', 'Escurece a tela atrás de diálogos (Background a 70%)')], BASE)

    d.h1('3. Degradês')
    d.p('Os degradês dão profundidade sem chamar atenção. Regras: **(1)** só do mesmo tom; **(2)** claro em cima, escuro embaixo; '
        '**(3)** aplicados em superfícies grandes (fundo, painéis, cards) e nunca em linhas de lista repetidas; '
        '**(4)** hover e estados continuam sendo cores sólidas com transição suave. Ângulos em CSS: 180deg = de cima para baixo; 90deg = da esquerda para a direita.')
    gradients(d, [
        ('Fundo da janela', 'linear-gradient(180deg, #160F22, #0C0812)', [('linear', [('#160F22', 0), ('#0C0812', 1)], V)]),
        ('Brilhos do fundo', 'Sobre o fundo, dois brilhos estáticos: radial no canto superior direito #8950D9 a 15% → transparente '
                             '(raio 60% × 55%) e radial no canto inferior esquerdo #C8A8F5 a 10% → transparente (raio 50%).',
         [('linear', [('#160F22', 0), ('#0C0812', 1)], V),
          ('radial', [('#268950D9', 0), ('#008950D9', 1)], (0.85, 0, 0.6, 0.55 * 3)),
          ('radial', [('#1AC8A8F5', 0), ('#00C8A8F5', 1)], (0.1, 1, 0.5, 0.5 * 3))]),
        ('Painéis (barra lateral, painel de edição)', 'linear-gradient(180deg, #1E1529, #150F1D) + borda #34204D',
         [('linear', [('#1E1529', 0), ('#150F1D', 1)], V)]),
        ('Cards', 'linear-gradient(160deg, #1F162B, #17101F) + borda #34204D, raio 10px',
         [('linear', [('#1F162B', 0), ('#17101F', 1)], (0, 0, 0.35, 1))]),
        ('Menus, tooltips e popups', 'linear-gradient(180deg, #2A1E3C, #1F162D) + borda #34204D',
         [('linear', [('#2A1E3C', 0), ('#1F162D', 1)], V)]),
        ('Widgets na área de trabalho', 'linear-gradient(180deg, #21172D, #160F1F)', [('linear', [('#21172D', 0), ('#160F1F', 1)], V)]),
        ('Janelas auxiliares e diálogos', 'linear-gradient(180deg, #181024, #0D0914)', [('linear', [('#181024', 0), ('#0D0914', 1)], V)]),
        ('Item selecionado (navegação)', 'linear-gradient(90deg, #5B3592 0%, #3E2760 55%, #34204D 100%) — o roxo se apaga para a direita',
         [('linear', [('#5B3592', 0), ('#3E2760', 0.55), ('#34204D', 1)], H)]),
        ('Brilho dos botões cheios', 'Camada por cima da cor sólida: linear-gradient(180deg, #FFFFFF33 0%, #FFFFFF0F 50%, transparente 51%, #0000001A 100%)',
         [('linear', [('#8950D9', 0), ('#8950D9', 1)], V),
          ('linear', [('#33FFFFFF', 0), ('#0FFFFFFF', 0.5), ('#00FFFFFF', 0.51), ('#1A000000', 1)], V)]),
        ('Nome do app (marca)', 'Texto com linear-gradient(90deg, #FFFFFF, #C8A8F5)', [('linear', [('#FFFFFF', 0), ('#C8A8F5', 1)], H)]),
    ], BASE)

    d.h1('4. Tipografia')
    d.p('Duas famílias, ambas gratuitas no Google Fonts (licença SIL Open Font License). Use os arquivos da fonte embutidos no app '
        'quando possível, para não depender da conexão.')
    d.data_table([1800, 1800, 6208], ['Uso', 'Família', 'Orientação'], [
        ['**Marca**', 'Geo', 'Somente no nome do aplicativo, **sempre em CAIXA ALTA** (ex.: HELMET DESK). Tamanho 21 px ao lado do logo. Pode receber o degradê branco → lilás.'],
        ['**Interface**', 'Roboto', 'Todo o resto: títulos, textos, botões, números. Pesos usados: 300 Light, 400 Regular, 500 Medium, 600 SemiBold, 700 Bold.'],
        ['**Fallback**', 'Segoe UI / system-ui', 'Usar quando a fonte não carregar.'],
        ['**Monoespaçada**', 'Consolas / JetBrains Mono', 'Só pontualmente, em código, logs ou dados técnicos.'],
    ])
    d.h2('Hierarquia')
    d.data_table([3000, 1700, 5108], ['Elemento', 'Tamanho', 'Peso / tratamento'], [
        ['Título de página', '24 px', '700 Bold; texto principal'],
        ['Subtítulo de página', '12 px', '400 itálico; texto secundário'],
        ['Título de seção / painel', '18 px', '600 SemiBold'],
        ['Números grandes (indicadores)', '28 px', '300 Light; algarismos tabulares (alinhados)'],
        ['Texto principal', '13–15 px', '400 Regular'],
        ['Rótulos de campo', '12 px', '600 SemiBold; texto secundário'],
        ['Texto auxiliar / metadados', '12 px', '400; texto secundário'],
        ['Seção em caixa alta (ex.: HOJE)', '11 px', '700 Bold; texto secundário'],
        ['Navegação lateral', '13,5 px', '500 Medium; ativo em 600'],
        ['Botões e controles', '13 px', '600 SemiBold'],
        ['Estados vazios', '12 px', '400 itálico; texto secundário'],
    ])
    d.p('Escala de referência (tokens): XS 11 · S 13 · M 15 · L 20 · XL 28 px.')

    d.h1('5. Layout e espaçamento')
    for t in ['Estrutura: barra lateral à esquerda (redimensionável, com modo compacto só de ícones de 64 px), cabeçalho simples e área principal; '
              'painel de edição desliza pela direita.',
              'Alinhamento, agrupamento e espaço livre em vez de separar tudo com bordas.',
              'Escala de espaçamento: **4, 8, 12, 16, 24 e 32 px**.',
              'Cards com padding interno de **16 px** (até 24 px em áreas de leitura).',
              'Raios: **6 px** (pílulas, botões de ícone, segmentos), **8 px** (botões, campos), **10 px** (cards, linhas da barra lateral), '
              '**12 px** (painéis, chips de etiqueta). Evitar formatos muito arredondados.',
              'Bordas de 1 px na cor Border; a barra lateral tem só a borda direita.']:
        d.bullet(t)

    d.h1('6. Componentes')
    d.data_table([2200, 3900, 3708], ['Componente', 'Aparência', 'Comportamento'], [
        ['**Botão primário**', 'Fundo Primary sólido + camada de brilho; texto branco 600; raio 8 px; padding 7 × 12 px.', 'Hover escurece para #7744BC com transição de 150 ms. Um por área.'],
        ['**Botão de perigo**', 'Fundo #FF6F91 + brilho; texto Background.', 'Hover #F2557C. Só para excluir/encerrar.'],
        ['**Botão secundário**', 'Fundo Surface; borda Border.', 'Hover Surface elevada.'],
        ['**Botão fantasma / ícone**', 'Transparente; texto ou ícone em texto secundário.', 'Hover: fundo Border e texto principal.'],
        ['**Campos de texto**', 'Fundo Surface; borda Border; placeholder secundário a 75%.', 'Hover borda #4C3270; foco borda Accent, sem glow.'],
        ['**Checkbox**', '18 px, raio 5, borda 1,5 px em texto secundário.', 'Hover borda Accent; marcado: fundo Primary e check branco.'],
        ['**Interruptor**', 'Trilho 38 × 22 px Border; bolinha 16 px.', 'Ligado: trilho Primary e bolinha branca, deslizando.'],
        ['**Cards**', 'Degradê de card + borda Border; raio 10.', 'Sem sombra. Agrupam informação relacionada.'],
        ['**Barra lateral**', 'Degradê de painel + borda direita.', 'Item ativo: marcador com degradê roxo que desliza entre os itens.'],
        ['**Segmentos e filtros**', 'Caixa Surface com borda; itens planos.', 'Selecionado: fundo Border, texto principal 600.'],
        ['**Chips de etiqueta**', 'Transparentes, borda Border, raio 12, 12 px.', 'Selecionado: fundo Border e borda Accent.'],
        ['**Menus e tooltips**', 'Degradê de popup + borda; raio 10 (menus) / 6 (dicas).', 'Aparecem com fade.'],
        ['**Diálogos**', 'Janela com degradê de diálogo, ícone em círculo suave (Accent ou Perigo a 20%).', 'Véu escuro atrás; botões objetivos.'],
        ['**Barra de rolagem**', '10 px, polegar Border, raio 3.', 'Hover #4C3270.'],
        ['**Botões da janela**', 'Planos, 44 × 32 px.', 'Hover Surface elevada; fechar fica #C42B1C.'],
        ['**Ícones**', 'Traço simples estilo Lucide, 2 px, cantos arredondados.', 'Usar rótulo quando a função não for óbvia.'],
    ])
    d.h2('Estados interativos')
    for t in ['**Default:** cores base com contraste suficiente.',
              '**Hover:** troca suave de cor ou superfície (150 ms); sem brilho nem animação chamativa.',
              '**Focus:** contorno Accent de 1,5 px, 2 px para fora, raio 9, surgindo com fade.',
              '**Ativo / selecionado:** degradê roxo (navegação) ou Primary (ação), conforme a importância.',
              '**Pressionado:** o controle encolhe para 97% por um instante.',
              '**Desabilitado:** opacidade 45–50%, sem confundir com o estado ativo.']:
        d.bullet(t)

    d.h1('7. Movimento')
    d.data_table([3000, 2000, 4808], ['Token', 'Valor', 'Uso'], [
        ['Rápido', '150 ms', 'Hover, foco, troca de cor'],
        ['Base', '220 ms', 'Entrada de listas, painéis e avisos'],
        ['Lento', '300 ms', 'Transições de página e painel lateral'],
        ['Curva', 'cúbica', 'ease-out para entrar, ease-in para sair, ease-in-out no resto'],
        ['Deslocamento', '8 / 16 / 24 px', 'Pequeno, padrão e painel'],
        ['Pressionar', 'escala 0,97', 'Botões ao clicar'],
        ['Surgir', 'escala 0,96 → 1', 'Diálogos e popups'],
    ])

    d.h1('8. O que fazer e o que evitar')
    dodont(d, ['Degradês do mesmo tom, claro em cima.', 'Brilho só nos botões cheios (primário e perigo).',
               'Manter Roboto na interface e Geo só na marca.', 'Bordas finas e espaçamento generoso.', 'Reaproveitar tokens e componentes.'],
              ['Degradês com cores diferentes (roxo → azul, arco-íris).', 'Glow, neon, vidro com desfoque e sombras pesadas (isso é do Violet Aero).',
               'Degradê em textos comuns (só no nome da marca).', 'Degradê em linhas de lista repetidas.', 'Cores saturadas em todos os elementos.'])
    d.p(f'**Acessibilidade:** não dependa só da cor para comunicar estados. Contrastes de referência: texto principal sobre o fundo {cr("#F5F1FA", "#100B18")}; '
        f'texto secundário sobre Surface {cr("#B9A8CB", "#191222")}; Accent sobre o fundo {cr("#C8A8F5", "#100B18")}. Foco de teclado sempre visível.')

    d.h1('9. Tokens CSS de referência')
    d.p('Ponto de partida para web, extensões e Electron. Os nomes podem mudar; os valores, não.')
    d.code(''':root {
  /* Base */
  --bg-primary: #100B18;   --bg-surface: #191222;   --bg-elevated: #221831;
  --color-border: #34204D; --color-border-hover: #4C3270;
  /* Marca */
  --color-primary: #8950D9; --color-primary-hover: #7744BC; --color-accent: #C8A8F5;
  /* Texto */
  --text-primary: #F5F1FA; --text-secondary: #B9A8CB;
  /* Semânticas */
  --color-success: #64D6A2; --color-warning: #F2C36B;
  --color-danger: #FF6F91;  --color-info: #78B7FF;

  /* Degradês */
  --grad-window: linear-gradient(180deg, #160F22, #0C0812);
  --grad-glow: radial-gradient(60% 55% at 85% 0%, #8950D926, transparent),
               radial-gradient(50% 50% at 10% 100%, #C8A8F51A, transparent);
  --grad-panel: linear-gradient(180deg, #1E1529, #150F1D);
  --grad-card: linear-gradient(160deg, #1F162B, #17101F);
  --grad-popup: linear-gradient(180deg, #2A1E3C, #1F162D);
  --grad-nav-selected: linear-gradient(90deg, #5B3592, #3E2760 55%, #34204D);
  --grad-button-sheen: linear-gradient(180deg, #FFFFFF33, #FFFFFF0F 50%,
                                       transparent 51%, #0000001A);
  --grad-brand: linear-gradient(90deg, #FFFFFF, #C8A8F5);

  /* Tipografia */
  --font-ui: "Roboto", "Segoe UI", system-ui, sans-serif;
  --font-brand: "Geo", "Roboto", sans-serif;  /* sempre text-transform: uppercase */

  /* Forma, espaço e movimento */
  --radius-sm: 6px; --radius-md: 8px; --radius-card: 10px; --radius-lg: 12px;
  --space-1: 4px; --space-2: 8px; --space-3: 12px; --space-4: 16px; --space-6: 24px; --space-8: 32px;
  --motion-fast: 150ms; --motion-base: 220ms; --motion-slow: 300ms;
  --ease-out: cubic-bezier(0.33, 1, 0.68, 1);
}

body   { background: var(--grad-glow), var(--grad-window); color: var(--text-primary); font-family: var(--font-ui); }
.card  { background: var(--grad-card); border: 1px solid var(--color-border); border-radius: var(--radius-card); padding: 16px; }
.btn-primary { background: var(--grad-button-sheen), var(--color-primary); color: #fff; border-radius: 8px;
               transition: background-color var(--motion-fast); }
.btn-primary:hover { background-color: var(--color-primary-hover); }
.brand { font-family: var(--font-brand); text-transform: uppercase;
         background: var(--grad-brand); -webkit-background-clip: text; color: transparent; }''')
    d.p('Fontes no Google Fonts: Roboto (300, 400, 500, 600, 700, itálico 400) e Geo (400).', after=0)
    path = os.path.join(OUT, 'Violet Terminal 2.0 - identidade visual (tema Clássico).docx')
    d.save(path)
    return path


# =====================================================================================
# 2) VIOLET AERO (Helmet Desk, tema Frutiger Aero noturno)
# =====================================================================================
def aero():
    BASE = '#140A2A'   # approximate backdrop behind the glass, for swatches of translucent tokens
    SKY = [('#06020E', 0), ('#0F0624', 0.4), ('#1E0C40', 0.75), ('#2E1360', 1)]
    d = Doc('GUIA DE IDENTIDADE VISUAL  /  DESIGN SYSTEM', 'VIOLET AERO  •  tema Frutiger Aero noturno')
    cover(d, 'VIOLET AERO', 'Identidade visual Frutiger Aero noturna, em roxo',
          'Vidro fumê  •  Gel brilhante  •  Aurora e bokeh  •  Roboto + Geo',
          'Releitura da estética Frutiger Aero (vidro, gel, luz e natureza digital dos anos 2006–2013) em versão escura e roxa, '
          'aplicada no tema Frutiger Aero do Helmet Desk. O roxo #8950D9 continua sendo a cor principal; o Aero está no material: '
          'vidro fumê, cápsulas de gel, bordas que pegam luz, aurora e orbes de bokeh ao fundo. Serve de referência para levar a mesma '
          'identidade a outros aplicativos e extensões.',
          [('DARK GLASS', 'Vidro fumê sobre céu noturno'), ('GEL + LUZ', 'Brilho no topo, glow no ativo'), ('ROBOTO + GEO', 'Interface + marca')])

    d.h1('1. Conceito e princípios')
    d.p('A interface é uma pilha de camadas de vidro flutuando sobre um céu noturno violeta com aurora. A luz vem sempre de cima: '
        'as bordas superiores ficam claras, há um brilho especular no topo dos painéis e dos botões, e o que está ativo ganha um leve brilho roxo. '
        'A primeira versão era clara e cansava a vista; esta é noturna, com texto claro sobre vidro escuro.')
    d.h2('Princípios fundamentais')
    for t in ['**Material, não cor:** a paleta continua roxa; o estilo Aero vem do vidro, do gel e da luz, não de azuis e verdes.',
              '**Luz de cima:** borda clara no topo, brilho especular na metade superior, sombra/escurecimento embaixo.',
              '**Profundidade em camadas:** céu e aurora → painéis de vidro → cards → controles de gel.',
              '**O ativo brilha:** glow roxo apenas em hover, foco e seleção; o repouso é calmo.',
              '**Leitura primeiro:** o vidro do conteúdo é mais opaco que o da barra lateral; texto sempre com contraste alto.',
              '**Desempenho:** desfoque e sombra só em camadas grandes e estáticas (fundo e painéis), guardadas em cache. '
              'Linhas de lista e cards nunca recebem efeitos.']:
        d.bullet(t)
    d.image(os.path.join(SHOTS, 'dark5', '1-hoje.png'), 6.3, 4.095, 'Referência: Helmet Desk, tela Hoje, tema Frutiger Aero (Violet Aero).')

    d.h1('2. Paleta de cores')
    d.h2('Escala roxa (base #8950D9)')
    scale = [('P50', '#F4EEFD'), ('P100', '#E4D6F9'), ('P200', '#C9AEF2'), ('P300', '#A982E6'), ('P400', '#8950D9'),
             ('P500', '#6F3CBC'), ('P600', '#522A93'), ('P700', '#351A63'), ('P800', '#1F0F3D'), ('P900', '#0E0620')]
    w = 9808 // 5
    ws = [w, w, w, w, 9808 - 4 * w]
    rows = []
    for i in (0, 5):
        rows.append([d.swatch_cell(c.lstrip('#'), cw, f'{n}  {c}') for (n, c), cw in zip(scale[i:i + 5], ws)])
    d.table(ws, rows, border="FFFFFF")
    d.h2('Cores de apoio')
    palette(d, [('Lavanda', '#D9C8F5', 'Bordas de luz, estrelas, realces pequenos'),
                ('Magenta suave', '#E58CC9', 'Só na aurora e em orbes de bokeh'),
                ('Água fria', '#48CAE4', 'Só na aurora, em raios de luz e orbes')], BASE)
    d.p('As cores de apoio nunca viram cor de botão, texto ou seleção: elas só aparecem no fundo.')
    d.h2('Tokens da interface')
    d.p('Vários tokens são translúcidos: a amostra mostra a cor já aplicada sobre o céu. Em CSS, o valor #RRGGBBAA traz a opacidade nos dois últimos dígitos.')
    palette(d, [('Fundo da janela', '#0B0516', 'Cor de base por trás do céu'),
                ('Background (rebaixado)', '#59080412', 'Blocos afundados dentro do vidro (linhas da barra lateral, segmentos)'),
                ('Surface', '#B3201434', 'Linhas e campos de vidro fumê'),
                ('Surface elevada', '#261A3E', 'Hover, menus e dicas (opaca, flutua sobre tudo)'),
                ('Border', '#4DA982E6', 'Divisores e bordas finas'),
                ('Primary', '#8950D9', 'Ação principal e seleção'),
                ('Primary hover', '#9A62E4', 'Hover: no Aero o roxo clareia e brilha'),
                ('Accent', '#C9A8F7', 'Links, datas, foco (≈ 9:1 sobre o vidro)'),
                ('Texto principal', '#F3EEFB', 'Títulos e conteúdo'),
                ('Texto secundário', '#BDAED6', 'Descrições e metadados'),
                ('Hover de vidro', '#26FFFFFF', 'Fundo de botões planos em hover (branco 15%)'),
                ('Borda de hover', '#4DFFFFFF', 'Borda dos botões planos em hover (branco 30%)'),
                ('Borda de janela', '#7A5BB3', 'Contorno de widgets e janelas auxiliares')], BASE)
    d.h2('Cores semânticas')
    palette(d, [('Sucesso', '#6BE0AE', 'Concluído, saldo positivo'), ('Aviso', '#F4C774', 'Atenção, vence em breve'),
                ('Erro / perigo', '#FF7D9B', 'Erro, exclusão, atrasado'), ('Informação', '#86C3FF', 'Dicas e avisos neutros')], BASE)
    d.p('São mais claras que no Violet Terminal para manter contraste de 6:1 ou mais sobre o vidro escuro.')

    d.h1('3. Materiais e degradês')
    d.h2('Céu e fundo')
    gradients(d, [
        ('Céu noturno', 'linear-gradient(165deg, #06020E 0%, #0F0624 40%, #1E0C40 75%, #2E1360 100%)', [('linear', SKY, (0, 0, 0.3, 1))]),
        ('Aurora', 'Faixas onduladas e largas, desfocadas (blur ≈ 70 px): P500 → P300 → magenta a 70%; magenta → P400 → água a 50%; '
                   'uma faixa fina de água a 45% embaixo e um brilho P500 a 35% no canto superior direito. Estática.',
         [('linear', SKY, (0, 0, 0.3, 1)),
          ('radial', [('#B39060E0', 0), ('#40A982E6', 0.5), ('#00A982E6', 1)], (0.45, 0.62, 0.7, 0.35)),
          ('radial', [('#80E58CC9', 0), ('#00E58CC9', 1)], (0.15, 0.3, 0.4, 0.3)),
          ('radial', [('#7348CAE4', 0), ('#0048CAE4', 1)], (0.9, 0.25, 0.3, 0.35))]),
        ('Bokeh', 'Orbes de 40–150 px com radial-gradient (origem em 35% 30%): centro claro a 25–40%, quase transparente a 85%, '
                  'anel mais claro na borda (30–40%). Em lavanda, magenta ou água.',
         [('linear', SKY, (0, 0, 0.3, 1)),
          ('radial', [('#40E4D6F9', 0), ('#0DA982E6', 0.85), ('#59C9AEF2', 0.97), ('#00C9AEF2', 1)], (0.3, 0.5, 0.11, 0.44)),
          ('radial', [('#33E58CC9', 0), ('#0AE58CC9', 0.85), ('#4DE58CC9', 0.97), ('#00E58CC9', 1)], (0.6, 0.5, 0.08, 0.32)),
          ('radial', [('#4048CAE4', 0), ('#0A48CAE4', 0.85), ('#5948CAE4', 0.97), ('#0048CAE4', 1)], (0.85, 0.5, 0.055, 0.22))]),
        ('Faixa de título', 'Tira de 40 px no topo: linear-gradient(180deg, #FFFFFF26 0%, #FFFFFF0F 50%, #FFFFFF05 50%, #FFFFFF0A 100%) '
                            'com borda inferior #FFFFFF26 (o "corte" do brilho do Windows Vista/7).',
         [('linear', SKY, (0, 0, 0.3, 1)),
          ('linear', [('#26FFFFFF', 0), ('#0FFFFFFF', 0.5), ('#05FFFFFF', 0.5), ('#0AFFFFFF', 1)], V)]),
    ], '#0B0516')
    d.p('Complementos: raios de luz finos (curvas de 1–3 px em lavanda a 20–40% e água a 20%) e estrelas (pontos brancos de 2–3 px, 50–80%).')

    d.h2('Vidro fumê')
    gradients(d, [
        ('Vidro da barra lateral', 'linear-gradient(180deg, #2A1C44B3, #160C28A6)', [('linear', SKY, (0, 0, 0.3, 1)), ('linear', [('#B32A1C44', 0), ('#A6160C28', 1)], V)]),
        ('Vidro do conteúdo (mais opaco)', 'linear-gradient(180deg, #241839CC, #120A20D1)', [('linear', SKY, (0, 0, 0.3, 1)), ('linear', [('#CC241839', 0), ('#D1120A20', 1)], V)]),
        ('Brilho especular', 'Topo do painel, 64 px de altura: linear-gradient(180deg, #FFFFFF2E, transparente)',
         [('linear', [('#CC241839', 0), ('#D1120A20', 1)], V), ('linear', [('#2EFFFFFF', 0), ('#00FFFFFF', 0.7)], V)]),
        ('Borda de luz (externa)', 'linear-gradient(180deg, #E4D6F9B3 0%, #A982E659 35%, #A982E633 100%) — clara no topo, violeta embaixo',
         [('linear', [('#E4D6F9', 0), ('#A982E6', 0.35), ('#6F4FA8', 1)], V)]),
        ('Card sobre o vidro', 'Fundo linear-gradient(180deg, #35264F8C, #1C12304D) e borda linear-gradient(180deg, #D9C8F580, #A982E626). Sem sombra.',
         [('linear', [('#CC241839', 0), ('#D1120A20', 1)], V), ('linear', [('#8C35264F', 0), ('#731C1230', 1)], V)]),
    ], '#120826')
    d.p('**Montagem de um painel de vidro (raio 12 px), de baixo para cima:** 1) contorno #8950D98C com glow #7A45D0 (desfoque 26 px, '
        'opacidade 55%); 2) preenchimento de vidro; 3) brilho especular no topo; 4) borda interna preta a 45%, 1 px para dentro; '
        '5) borda de luz externa. Na web, pode-se somar backdrop-filter: blur(18px) saturate(140%) atrás do painel.')

    d.h2('Gel (botões e seleção)')
    gradients(d, [
        ('Gel roxo (primário)', 'linear-gradient(180deg, #A97CEB 0%, #8950D9 55%, #6A34C2 100%); borda #2A1052; hover #B58DF0 → #955FE0 → #7440CC',
         [('linear', [('#A97CEB', 0), ('#8950D9', 0.55), ('#6A34C2', 1)], V), ('linear', [('#CCFFFFFF', 0), ('#1AFFFFFF', 0.5), ('#00FFFFFF', 0.5)], V)]),
        ('Gel vermelho (perigo)', 'linear-gradient(180deg, #EE7690 0%, #D63A5E 55%, #B21F40 100%); borda #4A0A1C',
         [('linear', [('#EE7690', 0), ('#D63A5E', 0.55), ('#B21F40', 1)], V), ('linear', [('#CCFFFFFF', 0), ('#1AFFFFFF', 0.5), ('#00FFFFFF', 0.5)], V)]),
        ('Gel de vidro (secundário)', 'linear-gradient(180deg, #4A3A66 0%, #2A1E40 50%, #160E26 100%); borda #8C7AAE; hover #5A4878 → #35274F → #1E1432',
         [('linear', [('#4A3A66', 0), ('#2A1E40', 0.5), ('#160E26', 1)], V), ('linear', [('#CCFFFFFF', 0), ('#1AFFFFFF', 0.5), ('#00FFFFFF', 0.5)], V)]),
        ('Brilho do gel', 'Metade de cima do botão: linear-gradient(180deg, #FFFFFF99, #FFFFFF14) com opacidade 80% (100% no hover)',
         [('linear', [('#8950D9', 0), ('#8950D9', 1)], V), ('linear', [('#99FFFFFF', 0), ('#14FFFFFF', 0.5), ('#00FFFFFF', 0.5)], V)]),
        ('Item selecionado', 'linear-gradient(180deg, #A77AEB 0%, #8752D8 48%, #6C36C4 50%, #7E46D2 100%) — brilho "partido" ao meio',
         [('linear', [('#A77AEB', 0), ('#8752D8', 0.48), ('#6C36C4', 0.5), ('#7E46D2', 1)], V)]),
        ('Campo de texto', 'linear-gradient(180deg, #07040F 0%, #130B21 18%, #1A1029 100%) — sombra interna no topo; borda #6E5A94',
         [('linear', [('#07040F', 0), ('#130B21', 0.18), ('#1A1029', 1)], V)]),
        ('Widgets e janelas auxiliares', 'linear-gradient(180deg, #35264F 0%, #231736 7%, #1A1029 50%, #130B20 100%); borda #7A5BB3',
         [('linear', [('#35264F', 0), ('#231736', 0.07), ('#1A1029', 0.5), ('#130B20', 1)], V)]),
        ('Nome do app (marca)', 'Texto com linear-gradient(180deg, #FFFFFF 0%, #FFFFFF 45%, #C9AEF2 100%) e glow #A982E6 (desfoque 10 px, 90%)',
         [('linear', [('#FFFFFF', 0), ('#FFFFFF', 0.45), ('#C9AEF2', 1)], V)]),
    ], '#120826')

    d.h1('4. Tipografia')
    d.p('As mesmas famílias do Violet Terminal, gratuitas no Google Fonts (SIL Open Font License). No Aero, os títulos ficam mais leves (Light) '
        'e os controles usam Medium, para combinar com a leveza do vidro.')
    d.data_table([1800, 1800, 6208], ['Uso', 'Família', 'Orientação'], [
        ['**Marca**', 'Geo', 'Só no nome do aplicativo, **sempre em CAIXA ALTA**, 21 px, com degradê branco → lilás e glow roxo suave.'],
        ['**Interface**', 'Roboto', 'Todo o resto. Pesos: 300 Light (títulos e números), 400 Regular, 500 Medium (controles), 700 Bold (seções em caixa alta).'],
        ['**Fallback**', 'Segoe UI / system-ui', 'Quando a fonte não carregar.'],
    ])
    d.data_table([3000, 1700, 5108], ['Elemento', 'Tamanho', 'Peso / tratamento'], [
        ['Título de página', '28 px', '300 Light; sem efeitos'],
        ['Subtítulo de página', '12 px', '400 itálico; texto secundário'],
        ['Título de seção / painel', '18 px', '500 Medium'],
        ['Números grandes', '28 px', '300 Light; algarismos tabulares'],
        ['Texto principal', '13–15 px', '400 Regular'],
        ['Rótulos de campo', '12 px', '500 Medium; texto secundário'],
        ['Seção em caixa alta (ex.: HOJE)', '11 px', '700 Bold; texto secundário'],
        ['Navegação lateral', '13,5 px', '500 Medium'],
        ['Botões e controles', '13 px', '500 Medium; branco nos botões de gel'],
    ])

    d.h1('5. Layout e espaçamento')
    for t in ['**Painéis flutuantes:** barra lateral e conteúdo são dois painéis de vidro separados, com 8–10 px de folga entre si e das bordas '
              'da janela, deixando o céu aparecer em volta (margens da barra lateral 10 · 2 · 10 · 8 px; conteúdo 10 · 10 · 10 · 6 px).',
              '**Sem linha divisória** entre a barra lateral e o conteúdo: o espaço entre os vidros já marca a borda.',
              '**Painel de edição** desliza **por dentro** do vidro do conteúdo, 1 px para dentro da borda, acompanhando a curva do canto; '
              'a transição fica recortada dentro do painel.',
              'Escala de espaçamento: **4, 8, 12, 16, 24 e 32 px**; cards com padding de **16 px**.',
              'Raios: **12 px** painéis de vidro · **10 px** cards e linhas da barra lateral · **15 px** botões de gel (cápsula) · '
              '**8 px** campos e segmentos · **7 px** botões de ícone · **5 px** botões da janela.',
              'Barra lateral compacta (só ícones): 64 px + 10 px das margens do vidro, para os ícones ficarem centralizados.']:
        d.bullet(t)

    d.h1('6. Componentes')
    d.data_table([2200, 3900, 3708], ['Componente', 'Aparência', 'Comportamento'], [
        ['**Botão primário**', 'Cápsula de gel roxo (raio 15, padding 7 × 16 px), borda interna branca a 35%, brilho na metade de cima, texto branco.',
         'Hover: gel mais claro + glow #B58DF0 (desfoque 16 px) em 180 ms. Pressionado: sombra interna escura em 80 ms.'],
        ['**Botão de perigo**', 'Cápsula de gel vermelho com o mesmo brilho.', 'Mesmas animações do primário.'],
        ['**Botão secundário**', 'Cápsula de vidro preto-violeta, borda #8C7AAE.', 'Hover clareia e ganha glow.'],
        ['**Botão fantasma / ícone**', 'Transparente; texto secundário.', 'Hover: "bolha" de vidro (branco 15%, borda branca 30%).'],
        ['**Campos de texto**', 'Vidro afundado (degradê com sombra no topo), borda #6E5A94, raio 8.', 'Foco: borda Accent.'],
        ['**Item selecionado**', 'Gel roxo com brilho partido, borda #2A1052 a 80%.', 'Marcador desliza entre os itens da navegação.'],
        ['**Segmentos e chips**', 'Caixa rebaixada #08041266 com borda branca a 20%.', 'Selecionado vira gel roxo (chips: borda Accent).'],
        ['**Cards**', 'Vidro mais claro + borda de luz no topo; raio 10.', 'Nunca recebem sombra ou desfoque (repetem muito).'],
        ['**Linhas da barra lateral**', 'Blocos rebaixados (#08041259) com borda branca a 15%.', 'Hover: bolha de vidro.'],
        ['**Botões da janela**', 'Cápsulas pequenas de vidro fumê (32 × 22 px; fechar 46 px) com brilho no topo, na faixa de título.',
         'Hover: clareia com glow; fechar vira gel vermelho.'],
        ['**Menus e dicas**', 'Superfície elevada opaca #261A3E com borda.', 'Fade de entrada.'],
        ['**Widgets e diálogos**', 'Janela opaca com degradê de vidro, faixa de brilho no topo e borda #7A5BB3.', 'Véu #060210 a 70% atrás de diálogos.'],
        ['**Ícones**', 'Traço estilo Lucide, 2 px, cor do texto.', 'Accent quando ativos.'],
    ])
    d.h2('Estados interativos')
    for t in ['**Repouso:** vidro calmo, brilho do gel a 80%.',
              '**Hover:** brilho do gel a 100% e glow roxo (180 ms para entrar, 200 ms para sair); botões planos viram bolha de vidro.',
              '**Foco:** contorno Accent de 1,5 px, raio 9, com fade.',
              '**Pressionado:** escurece por dentro (80 ms) e encolhe para 98%; volta em 160 ms.',
              '**Desabilitado:** opacidade 50% e brilho a 40%.']:
        d.bullet(t)

    d.h1('7. Movimento e desempenho')
    d.data_table([3000, 2000, 4808], ['Token', 'Valor', 'Uso'], [
        ['Rápido / Base / Lento', '150 / 220 / 300 ms', 'Hover; entradas; páginas e painel lateral'],
        ['Curva', 'cúbica', 'ease-out para entrar, ease-in para sair'],
        ['Gel hover', '180 ms / 200 ms', 'Entrada / saída do glow e do brilho'],
        ['Gel pressionado', '80 ms / 160 ms', 'Escurecer / voltar'],
        ['Pressionar', 'escala 0,98', 'Mais suave que no Violet Terminal'],
        ['Deslocamento', '8 / 16 / 24 px', 'Pequeno, padrão e painel'],
    ])
    for t in ['O fundo (céu, aurora, bokeh) é **estático** e desenhado uma vez; a aurora animada foi removida porque travava as transições.',
              'Desfoque e sombras só no fundo e nos dois painéis grandes, guardados como imagem (cache); a aurora é renderizada em meia resolução.',
              'Durante as transições, o elemento animado é guardado em cache e liberado no fim.',
              'Nada de sombra, glow ou desfoque em linhas de lista, cards ou chips.']:
        d.bullet(t)

    d.h1('8. O que fazer e o que evitar')
    dodont(d, ['Luz vindo de cima: borda clara e brilho no topo.', 'Vidro só em camadas grandes; conteúdo mais opaco.',
               'Glow roxo só em hover, foco e seleção.', 'Fundo rico (aurora, bokeh), interface calma.', 'Roxo como cor principal; aqua e magenta só no fundo.'],
              ['Versão clara do vidro (cansa a vista).', 'Desfoque ou sombra em itens repetidos.', 'Animações contínuas no fundo.',
               'Linha divisória colorida entre os painéis.', 'Azul e verde do Aero original como cor de marca.'])
    d.p(f'**Acessibilidade:** contrastes de referência sobre o vidro do conteúdo: texto principal {cr("#F3EEFB", over("#CC241839", "#140A2A"))}, '
        f'texto secundário {cr("#BDAED6", over("#CC241839", "#140A2A"))}, Accent {cr("#C9A8F7", over("#CC241839", "#140A2A"))}; '
        f'texto branco sobre o gel roxo {cr("#FFFFFF", "#8950D9")}. Não dependa só do brilho para indicar estado.')

    d.h1('9. Tokens CSS de referência')
    d.p('Ponto de partida para web, extensões e Electron. Na web, backdrop-filter dá o desfoque real atrás do vidro (o app desktop simula com transparência).')
    d.code(''':root {
  /* Escala roxa */
  --p50:#F4EEFD; --p100:#E4D6F9; --p200:#C9AEF2; --p300:#A982E6; --p400:#8950D9;
  --p500:#6F3CBC; --p600:#522A93; --p700:#351A63; --p800:#1F0F3D; --p900:#0E0620;
  --lavender:#D9C8F5; --magenta:#E58CC9; --aqua:#48CAE4;   /* só no fundo */

  /* Tokens */
  --bg-window:#0B0516; --bg-recessed:#08041259; --bg-surface:#201434B3; --bg-elevated:#261A3E;
  --color-border:#A982E64D; --color-primary:#8950D9; --color-primary-hover:#9A62E4;
  --color-accent:#C9A8F7; --text-primary:#F3EEFB; --text-secondary:#BDAED6;
  --glass-hover:#FFFFFF26; --glass-hover-rim:#FFFFFF4D;
  --color-success:#6BE0AE; --color-warning:#F4C774; --color-danger:#FF7D9B; --color-info:#86C3FF;

  /* Céu */
  --sky: linear-gradient(165deg, #06020E, #0F0624 40%, #1E0C40 75%, #2E1360);
  --titlebar: linear-gradient(180deg, #FFFFFF26, #FFFFFF0F 50%, #FFFFFF05 50%, #FFFFFF0A);

  /* Vidro */
  --glass: linear-gradient(180deg, #2A1C44B3, #160C28A6);
  --glass-strong: linear-gradient(180deg, #241839CC, #120A20D1);
  --glass-rim: linear-gradient(180deg, #E4D6F9B3, #A982E659 35%, #A982E633);
  --glass-specular: linear-gradient(180deg, #FFFFFF2E, transparent 64px);
  --card: linear-gradient(180deg, #35264F8C, #1C123073);
  --field: linear-gradient(180deg, #07040F, #130B21 18%, #1A1029);

  /* Gel */
  --gel-purple: linear-gradient(180deg, #A97CEB, #8950D9 55%, #6A34C2);
  --gel-purple-hover: linear-gradient(180deg, #B58DF0, #955FE0 55%, #7440CC);
  --gel-red: linear-gradient(180deg, #EE7690, #D63A5E 55%, #B21F40);
  --gel-glass: linear-gradient(180deg, #4A3A66, #2A1E40 50%, #160E26);
  --gel-shine: linear-gradient(180deg, #FFFFFF99, #FFFFFF14);
  --nav-selected: linear-gradient(180deg, #A77AEB, #8752D8 48%, #6C36C4 50%, #7E46D2);

  --font-ui: "Roboto", "Segoe UI", system-ui, sans-serif;
  --font-brand: "Geo", "Roboto", sans-serif;   /* sempre uppercase */
  --radius-panel:12px; --radius-card:10px; --radius-gel:15px; --radius-field:8px;
  --motion-fast:150ms; --motion-base:220ms; --motion-slow:300ms;
}

body { background: var(--sky); color: var(--text-primary); font-family: var(--font-ui); }

.glass {                                 /* painel de vidro */
  position: relative; border-radius: var(--radius-panel);
  background: var(--glass-specular) top / 100% 64px no-repeat, var(--glass-strong);
  backdrop-filter: blur(18px) saturate(140%);
  box-shadow: 0 0 26px #7A45D08C, inset 0 0 0 1px #00000073;
}
.glass::before {                         /* borda de luz */
  content: ""; position: absolute; inset: 0; border-radius: inherit; padding: 1px;
  background: var(--glass-rim);
  -webkit-mask: linear-gradient(#000 0 0) content-box, linear-gradient(#000 0 0);
  -webkit-mask-composite: xor; mask-composite: exclude; pointer-events: none;
}

.btn-gel {                               /* botão primário */
  position: relative; border-radius: var(--radius-gel); padding: 7px 16px;
  background: var(--gel-purple); color: #fff; font-weight: 500;
  border: 1px solid #2A1052; box-shadow: inset 0 0 0 1px #FFFFFF59;
  transition: box-shadow 180ms, transform 80ms;
}
.btn-gel::after {                        /* brilho na metade de cima */
  content: ""; position: absolute; left: 2px; right: 2px; top: 2px; height: 50%;
  border-radius: 13px 13px 6px 6px; background: var(--gel-shine); opacity: .8;
}
.btn-gel:hover  { background: var(--gel-purple-hover); box-shadow: 0 0 16px #B58DF0, inset 0 0 0 1px #FFFFFF59; }
.btn-gel:active { transform: scale(.98); }

.brand { font-family: var(--font-brand); text-transform: uppercase;
         background: linear-gradient(180deg, #fff, #fff 45%, #C9AEF2);
         -webkit-background-clip: text; color: transparent; filter: drop-shadow(0 0 5px #A982E6E6); }''')
    d.p('Fontes no Google Fonts: Roboto (300, 400, 500, 700, itálico 400) e Geo (400).', after=0)
    path = os.path.join(OUT, 'Violet Aero - identidade visual (tema Frutiger Aero).docx')
    d.save(path)
    return path


print(classic())
print(aero())

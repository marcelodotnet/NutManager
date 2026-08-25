# NutManager UI Design System

## Status and purpose

The shared Windows-first presentation foundation is implemented. It modernizes the Avalonia shell without changing NUT, safe-write, remote-transport, credential, driver, or privilege boundaries. T24A applies that foundation to managed-server profiles and T24B applies it to current operational pages; semantic configuration work remains T25+.

## Implemented shared presentation layer

`NutManager.App/Presentation` owns the reusable App-only presentation resources:

```text
Presentation
├── Themes
│   ├── NutColors.axaml
│   ├── NutMetrics.axaml
│   ├── NutMotion.axaml
│   ├── NutTypography.axaml
│   ├── NutControlStyles.axaml
│   ├── NutShellStyles.axaml
│   └── NutIcons.axaml
└── Controls
    ├── NutConnectionIndicator
    ├── NutStatusBadge
    └── NutReviewDrawerHost
```

`App.axaml` composes these dictionaries and retains the page data templates. Theme resources, component styles, and icon geometries are not duplicated in the window or page views. The shared controls contain presentation only: they do not poll, execute administrative operations, inspect files, or write configuration.

## Approved design references

`00_overview_reference.png` and `00_ups_conf_reference.png` are the primary fidelity targets at 1536×1024. They define shell proportions, surface hierarchy, spacing, typography, icon scale, selection treatment, semantic colors, and the future review-drawer proportions. `01_configuracoes.png` through `09_sobre.png` are secondary storyboards for information architecture and reusable component patterns; they are not evidence that unsupported commands or backends exist.

T24 established the shared shell against those primary references. T24B supplies responsive current-page composition for Overview, Devices, Diagnostics, and Administration without inventing unsupported health, history, test, or service capabilities. T25–T28 now populate the graphical configuration and review-drawer foundation; final cross-surface hardening remains T29.

## Shell and responsive states

The shell has three presentation states:

| State | Width | Navigation and review |
| --- | --- | --- |
| Wide | >= 1200 px | Expanded sidebar and an optional 360–420 px review drawer share space with forms. |
| Medium | 860–1199 px | One or two form columns; sidebar may collapse and review may overlay. |
| Compact | < 860 px | Overlay navigation, single-column forms, overlay review, and no ordinary horizontal scrolling. |

The left sidebar has Expanded (currently 220 px), Collapsed (72 px), and Overlay states. In Wide, the chevron, header button, or `Ctrl+B` changes the persisted preference. Medium deliberately projects Collapsed and does not mutate a preference that would have no immediate visual effect. Compact projects navigation as an overlay opened by the header button or `Ctrl+B`; closing it or returning to a wider layout does not overwrite the persisted Expanded/Collapsed preference. The selected item uses a subtle product-owned surface, a 3 px accent bar, and accent foreground—never literal selected text. Collapsed items keep tooltips and accessible names. Sidebar preference is non-secret UI preference data.

The review presentation mapper defines Hidden, Collapsed, Expanded, and Overlay states, and `NutReviewDrawerHost` provides the shared 368 px content host. T25 connects an optional generic semantic-review presentation: deterministic changes, localized validation issues, custom parameters, activation information, and redacted generated-preview lines. With no semantic draft it remains Hidden. The presentation is read-only and has no Apply command; the T26–T28 forms provide draft actions while persistence continues through the existing safe-write pipeline.

## Header, theme, and visual tokens

The header shows the active runtime profile/UPS endpoint and a 12 px connection core with a 24 px soft halo through `NutConnectionIndicator`. It observes the existing Overview/polling state; it does not create another client, timer, or polling loop. Status is always accompanied by localized visible detail text rather than color alone:

- green: Connected and Fresh;
- yellow/orange: Connecting, Reconnecting, Stale, or pending;
- red: Disconnected, failure, or critical condition;
- gray: no active profile or unavailable context.

The connection light combines a stable 8 px semantic core, a static ambient shadow, and a second Composition-driven halo. Healthy alone uses the approved 2.0-second breathing pulse; Pending, Critical, and Unavailable remain static in their amber, red, and gray semantics. The compositor adds no UI-thread timer, state transitions and visual-tree detach explicitly stop the old animation, and adjacent text always carries the same state without relying on colour or motion. Aggressive flashing is prohibited. Blue/cyan is the normal application accent. Green is reserved for healthy/success, while yellow/orange and red retain warning and error meaning. Mock mode is displayed persistently through the warning-toned `NutStatusBadge`.

The header uses a compact PathIcon sun/moon toggle. System theme remains available in **Settings → Appearance & Language**; clicking the header control from System makes the next Light/Dark preference explicit from the effective theme.

The resource dictionaries define spacing 4/8/12/16/20/24/32; radii 6/8/12/16; a 38 px standard control height; shell/page/card measurements; and 140/180/220 ms motion tokens. Typography uses Segoe UI Variable with Segoe UI and Arial fallbacks: product title 21, page title 27, section title 18, body 14, and metadata 12. Reusable PathIcon geometries replace text glyphs in shell navigation and theme controls.

`NutAccentBrush`, `NutAccentBrightBrush`, and `NutSelectionBrush` are product-owned tokens. `NutColors.axaml` supplies intentional Light and Dark surface/text palettes plus invariant accent, cyan, healthy, warning, critical, purple, focus, and unavailable semantics. Shell navigation, Administration selectors, and selected `ListBoxItem` presentation use these resources rather than the Windows accent, so red is never normal selection. Localized presentation properties replace raw enum text on touched summaries. Option controls introduced by later tasks continue to use localized presentation objects, not `Enum.ToString()` values.

The shell follows the one-scroll-owner rule: `MainWindow` contains no page-level `ScrollViewer`; its content host gives the selected page the available space, and each page owns one vertical scroll surface. Medium and Compact modes reduce shell content padding; Medium projects collapsed navigation and Compact uses overlay navigation. T24B replaces rigid master/detail grids with responsive projection and wrap-based cards without ordinary horizontal scrolling.

## Managed-server Settings surface

T24A uses the shared card, typography, spacing, border, product-selection, healthy, warning, and critical resources rather than introducing page-local colors. Managed servers appear as useful cards with endpoint, localized Local/Remote and ReadOnly/Manage summaries, transport, and active status. The editor uses a wide list/editor split and projects to a single column below its compact threshold, retaining one vertical scroll owner and no ordinary horizontal scroll. Inline validation and connection-test results always include text; the dirty-draft decision is keyboard-operable and does not rely on color.

## Administration information architecture

```text
Administration
├── NUT Configuration
│   ├── General (nut.conf)
│   ├── UPS (ups.conf)
│   ├── Server (upsd.conf)
│   ├── Users (upsd.users)
│   └── Monitoring (upsmon.conf)
├── Windows Service
├── Devices and Drivers
└── Remote Access
```

T24B preserves the existing-entry fallback and reviewed T14 preview inside NUT Configuration. T25 supplies the generic semantic draft/review/generated-preview foundation without adding a writer. T26 uses that foundation for graphical `ups.conf`. T27 adds dedicated General (`nut.conf`) and Server (`upsd.conf`) surfaces with Basic/Advanced/Custom groups, wrapping LISTEN/TLS/custom rows, textual accessible actions, and the same page-level scroll owner. T28 completes the supported set with dedicated Users (`upsd.users`) and Monitoring (`upsmon.conf`) forms, including change-only password presentation and repeated monitor/notification rows.

## Approved visual fidelity (T27A)

T27A aligns the rendered application with the approved visual references without changing domain, transport, write, privilege, or hardware safety boundaries. Its functional hardening is limited to presentation/runtime defects found during visual validation, including latest-selection-wins configuration navigation and passive Windows metadata discovery.

`Presentation/Themes` is the single source for the visual language. `NutColors.axaml` defines an explicit surface hierarchy — window, shell, surface, elevated, interactive, selected — plus border, text, accent and semantic families in both themes, so cards no longer carry the same visual weight and navigation selection is a restrained accent bar and low-contrast surface instead of a saturated block. `NutTypography.axaml` separates page title, section title, card title, label, metadata and the dominant metric readout. `NutMetrics.axaml` owns spacing, radii, icon sizes and shell dimensions. `NutControlStyles.axaml` and `NutShellStyles.axaml` restyle cards, buttons, inputs, lists, tabs, badges, the title bar, navigation and the profile card so surfaces stop reading as default Fluent controls.

`NutIcons.axaml` declares every semantic icon name and a fallback drawing for each, as `StreamGeometry` on a 24×24 grid, covering navigation, configuration domains, metrics, connectivity, security, service control, actions, chevrons, theme and window chrome. `NutIconLibrary.cs` replaces all of them at start-up with geometry from Material Icons, so the library is what the product actually draws and the catalog is what keeps a name resolving if a future version of the library drops a kind. Emoji, pictographic text and raster images are not used as icons. Semantic icon colour is always redundant with text.

The catalog is deliberately the only thing views know about. A view asks for `NutIconServer`, not for
a library's symbol, so where the drawing comes from can change without touching the interface.

The window uses `WindowDecorations="BorderOnly"` so product identity, connection state, the theme control and the window buttons share one integrated bar instead of a separate Windows title strip. Drag, double-click maximise, minimise, restore and close remain standard Avalonia window operations with no platform interop.

Motion is defined in `NutMotion.axaml` and stays within roughly 140–320 ms for interaction feedback: navigation selection, hover, card and input state, drawer content, tab underline, theme selection, load-gauge sweep and battery value transitions. The semantic status halo is the only looping animation, is purely decorative, and never carries state on its own. No animation timer, background worker or polling loop is introduced for decoration.

Overview is composed as a UPS dashboard: battery with animated charge bar, semicircular load gauge built from the native `Arc` shape, runtime with its raw NUT reading, input and output, UPS state with its status tokens, and connection. Every reading is projected from the current snapshot; a missing NUT variable keeps its card composition and shows the unavailable label rather than a substituted value, and this is pinned by tests.

## Accessibility and terminology

Icon-only shell controls have `AutomationProperties.Name`, tooltips, and the shared focus-visible border. Connection state includes text as well as color. Opening Compact navigation transfers focus to its localized close button, cycles keyboard navigation inside the overlay, and disables the shell controls behind the scrim. The overlay can be closed without changing the saved navigation preference, and `Ctrl+B` remains available in applicable states. Critical warnings must always include explicit text. The product displays **SFTP**; internal contracts may retain `SshSftp`.

Mock/demo state is an unambiguous persistent badge, never merely an incidental checkbox value.

All layouts introduced by T24A–T29 must be validated in both official cultures as those tasks are implemented. See [Localization](LOCALIZATION.md) and [Graphical NUT configuration](GRAPHICAL-NUT-CONFIGURATION.md).

## Administration navigation: two rows, not three columns (T32)

The administration page navigates horizontally. Section tabs across the top, a file strip under
them, and the editor below at full width.

It was three parallel vertical rails before — the shell sidebar at 274 px, the section rail at
343 px and the file rail at 283 px — so **900 px, 46% of a 1938 px window, was navigation chrome
before any content**, and the editor got 942 px. Worse vertically: with a file open, the page spent
its height on a title, a context card, an installation card, a breadcrumb, a file header and a
metadata strip, so **not one editable field was visible without scrolling**. The thing you came to
do was the last thing on screen.

Horizontally the same navigation costs two rows and the editor gets about 1571 px, a 67% gain, with
the first controls on screen when the file loads.

Three cuts came with it:

- **The installation card became one line.** Install directory, config directory and version are
  facts you confirm once and stop reading; they had no business holding the top of the page.
- **The breadcrumb went.** Page, section and file name repeated what the section tab and the
  selected file chip already said, immediately above the file's own title. Three statements of one
  fact is noise, not orientation.
- **The availability line under each section moved into its tooltip.** It is a sentence, and a
  sentence under every tab turns a strip into a second rail lying on its side. Each section states
  its own availability inside its panel.

Both strips are built from the same pieces as the shell navigation item — `NutAccentBrush` for
selection, `NutSelectedSheenBrush` for the selected surface, the glass hover — so the sidebar, the
tabs and the chips read as one idea at three scales. A horizontal strip marks its current item with
an underline rather than a left bar: a bar down the left of a wide short chip reads as a bullet
point, not a selection.

### One segmented control, and no fold

The five files are 108 px squares butted against each other inside a single frame, divided by a
hairline. The frame carries the glass, the outline and the rounding; the tiles are transparent and
draw only their own right-hand divider, so adjacent tiles share one line rather than drawing two.
The last divider is removed by the frame's clip — the strip overhangs by exactly the pixel that
divider occupies, which is tidier than asking a style to know which item is last.

Uniform squares rather than pills fitted to their labels: five equal tiles read as one set of five
files of equal standing, while five differently sized capsules read as unrelated buttons that happen
to be adjacent, and the eye has to measure each one to see it is a peer of the others.

**The fold was removed entirely**, and with it the whole of what T31 built for it: the toggle, the
`ConfigurationRailPreference` persisted in settings, the effective-state property separate from that
preference, the width threshold that folded the strip regardless, and the resize handler that
rebuilt the page's grid. A strip is worth collapsing while it is a column eating 283 px of width; as
one row of squares there is nothing worth reclaiming, and a switcher that can hide the thing you
switch with is a way to get lost rather than a way to save room. Nothing was kept "just in case" —
`NothingIsLeftOfTheFoldThatWasRemoved` fails if any piece of it reappears in the view, the styles,
the view model or the settings record.

Existing settings files that still carry `configurationRailPreference` load unchanged: the
serializer ignores members it does not know, so no migration was needed for a field that only
shrank out of the schema.

Each file keeps its own icon: `NutIconGeneral`, `NutIconUps`, `NutIconServer`, `NutIconUsers`,
`NutIconMonitoring`. The label under it is the category, not the file name, so the accessible name
and tooltip carry both. Selection is never colour alone — the filled segment, the accent underline
and a semibold label carry it together, and the underline is a separate layer because the tile's one
border is already spent on the divider and a single `Border` cannot hold two colours on two sides.
The selected tile's icon pops once when it becomes current; nothing in the strip loops.

## Glass surfaces and the two-tone window (T31)

The window is transparent with an `ExperimentalAcrylicBorder` behind the entire shell. This is not
decoration for its own sake: Avalonia has no backdrop filter, so before the pane existed the
translucent cards were tinting a flat colour and the effect was invisible. The transparency hint
degrades from acrylic to Mica to plain blur.

That framework limitation is still real — nothing in Avalonia blurs what is already on the page — but
it is no longer the end of the story. `NutBackdropBlur`, below, does it with a custom draw operation.

### The backdrop actually shows the desktop (T32)

For a while it did not, and the reason is worth recording because nothing about it was visible in
the markup. The acrylic pane was there and correctly configured, but three things in front of it
were opaque: `TintColor` was a near-black navy at `TintOpacity="1"`, so the material blurred a solid
colour rather than anything behind the window, and the content border painted `NutWindowBrush` —
also opaque — straight over the result. The window was a dark blue slab built out of an acrylic
brush, and measuring a horizontal strip of background gave the same value at every x, which is the
proof: real blur of real content varies across the pane.

Three changes fixed it, and they have to be made together — any one alone leaves the pane covered:

| | Was | Is |
| --- | --- | --- |
| `NutAcrylicTintColor` | `#05080E` (navy) | `#000000` — black tints nothing |
| `TintOpacity` / `MaterialOpacity` | `1` / `0.62` | `0.25` / `0.12` |
| `NutWindowBrush` (dark) | `#05080E` opaque | `#73000000` — a wash, not a floor |

**What shows through is the desktop wallpaper, and only that.** Windows composes this backdrop from
the wallpaper, not from the windows behind ours: DWM does not hand one process the pixels of
another. So the effect is invisible whenever something else is maximised behind the window, which is
exactly the case that makes it look broken — and it is not. Measured with the desktop showing, a
horizontal strip of background varies by 35 levels across the pane; measured with the same window
over another application, it is flat.

The alpha on `NutWindowBrush` is not decoration either. A see-through background inherits whatever
wallpaper the machine has, and body text cannot depend on that being dark, so the wash is the
contrast floor. It is tuned rather than guessed: against the brightest point the wallpaper pushed
into the pane, primary text measures 10.6:1 and secondary 5.1:1, both clear of WCAG AA's 4.5. At the
value tried before it (`#40000000`) secondary fell to 3.7:1, which is why the wash sits where it
does. `NutTextMutedBrush` reaches only 2.8:1 there and is therefore never used directly on the page
background — it belongs inside cards, whose own alpha supplies the contrast.

The header, footer and sidebar keep their own opaque or near-opaque brushes: the change was scoped
to the page background, so the chrome around it still reads as chrome.

Transparency only reads when the layers differ, which is why the palette is deliberately two-tone:

| Token | Role | Dark | Light |
| --- | --- | --- | --- |
| `NutAcrylicTintColor` | the pane behind everything | `#000000` | `#DDE4EF` |
| `NutGlassSurfaceBrush` | cards, rail, panels | `#8C3A4A66` | `#A6FFFFFF` |
| `NutGlassBorderBrush` | the pane's edge | `#40FFFFFF` | `#73FFFFFF` |
| `NutGlassSheenBrush` | top-edge highlight | — | — |
| `NutGlassSurfaceHoverBrush` | the pane under the pointer | `#A6485A78` | `#D9FFFFFF` |
| `NutGlassBorderHoverBrush` | its edge, catching light | `#73FFFFFF` | `#B39DB6DA` |
| `NutGlassRowHoverBrush` | a row on that pane | `#B35F749B` | `#D9DCE9FA` |

With the backdrop and the surfaces on the same navy, a 70% panel still looked opaque; the backdrop
is now the darkest value in the window and the surfaces lift well clear of it.

### Hover

Glass responds to the pointer. There is one response, shared by every surface actually made of
glass — `Border.nut-card` and `Button.nut-file-chip` — and it is only those: the surface lightens and
its edge comes up over 180 ms with a cubic ease. Nothing moves, resizes or gains a shadow, so a pane
reacting cannot shift the page around it or clip its own rounded corner against a scroll viewer.

Both halves are needed. A surface that brightens with no edge change reads as a colour bug rather
than as light landing on a pane; an edge that brightens alone reads as a selection.

Three things it deliberately does **not** do:

- **The step is small.** Glass that jumps to near-white under the pointer stops reading as a material
  and starts competing with the accent bar that marks the actual selection.
- **Rows get their own rung.** The pointer is on a row and its pane at the same time, so a row
  sharing the pane's hover tint would vanish into the surface exactly when pointed at.
  `NutGlassRowHoverBrush` sits one step above, and rows never take the edge highlight — their left
  border is the three-pixel selection bar, and lighting it would claim the row is the current page.
- **Containers stay inert.** The sidebar and the shell chrome are glass but are not hovered as
  objects; brightening a full-height panel every time the pointer crosses it toward a navigation item
  is noise, not feedback. Their rows carry the response instead.

In both themes the border hover moves in the direction that makes an edge visible: brighter white on
dark, and a tinted blue on light, where white on near-white is not an edge at all. Both hover brushes
are solid colours because `BrushTransition` interpolates between solid brushes and switches outright
between anything else — a gradient would make the edge snap on, which is the one thing the effect
must not do.

Hover never overrides state. Pressed and selected are declared after hover in `NutShellStyles.axaml`,
because Avalonia resolves equally matching setters by declaration order; `RowHoverNeverOverridesSelectedPressedOrDisabled`
pins that ordering, since nothing about it is visible at the call site.

The language follows Apple's glass rather than a tinted panel: frosted and cool instead of tinted
navy, a thin white hairline instead of a coloured border — on those panes the rim is light catching
an edge, not a drawn outline — and larger continuous radii, which is half of what makes a surface
read as glass at all. Badge fills and the navigation selection sheen carry alpha for the same
reason, so they read as tinted glass over the pane rather than as painted chips.

Foreground colours are untouched throughout. The alpha on every surface stops where body text would
start losing contrast: the effect is never allowed to cost legibility.

The acrylic pane breathes over sixteen seconds with a narrow swing. It and the connection light are
the only two continuous animations in the application, and neither is a control style — a looping
style would apply to every instance of a control, which remains forbidden and is what the
interaction tests defend.

## Frosted page edges (T37)

The page dissolves into the title bar and into the footer instead of ending at a cut. Two permanent
overlays sit at each end of the content area, painted over the page and taking no part in layout: a
frost pass and a tint pass, in that order, which is the order of the real material — blur the
backdrop, then colour it.

### NutBackdropBlur

Avalonia has no backdrop filter, and the three things that look like one are not. A blur effect blurs
the element together with its own children. The acrylic material reaches the window's backdrop rather
than the application's content. A `VisualBrush` pointed at a visual already in the tree does not paint
it at all.

What works is the one thing Skia exposes directly. By the time a control renders, the surface already
holds everything drawn before it, so a snapshot of that surface **is** the backdrop — the real pixels
of the page underneath, not a second rendering of it. Blurring the snapshot and painting it back over
the same rectangle is a backdrop filter in the only sense that matters here.

Two properties of the implementation constrain how it may be used, and both are load-bearing:

- **It reads the frame buffer every time it renders.** It belongs on a small, fixed band and not on a
  large or frequently invalidated surface.
- **Its falloff cannot be an `OpacityMask`.** A mask puts the control on its own render layer, and the
  layer the snapshot then reads is empty — the blur disappears silently while everything else still
  renders. The gradient is applied inside the draw operation instead.

### The tint, and why it is the header's own colour

The tint gradient is `NutHeaderBrush`, the colour the title bar and the footer already carry, so the
page dissolves into the bar it is running under rather than darkening against it. Every stop holds
that same RGB and varies only in alpha: a ramp ending in transparent black interpolates towards black
on the way there and dirties the middle of the fade.

### Two things measurement settled

The blur radius stops doing anything past roughly forty on a band this shallow. What reads as a weak
effect is not fixed by raising it — the mask weights are what matter, because at half alpha the sharp
original shows through the frost and its edges are what the eye picks up.

The band has to stop above the page title. Any perceptible blur reaching further sits on the title and
softens it at rest, and content at rest must never look degraded.

The haze inside the band is the blur itself: bright text redistributed into the dark gaps around it,
which raises the darkest pixel from near zero to around forty. No parameter separates the two, because
they are the same operation. The tint reaches far enough down to absorb it.

## Icon system policy (T32)

Every icon the application draws comes from `Material.Icons.Avalonia` 3.0.2 (MIT). T32 investigated
the maintained options and adopted one.

| Option | Licence | Rendering | Outcome |
| --- | --- | --- | --- |
| `Material.Icons.Avalonia` 3.0.2 | MIT | vector path | **chosen** |
| `FluentIcons.Avalonia` 2.1.337 | MIT | font glyph | rejected |
| Continue vendoring `fluentui-system-icons` | MIT | vector path | rejected |

`FluentIcons.Avalonia` renders through a font and exposes no geometry, so it cannot fill a catalog:
it would require `<ic:FluentIcon>` elements in the views, putting a library reference in every
surface and losing the single point of resolution. `Material.Icons.Avalonia` exposes path data
through `MaterialIconDataProvider.GetData`, which is what lets one adapter fill the catalog while the
views go on asking for semantic names.

Continuing to vendor was rejected because it could not reach the whole product: hand-copied geometry
covered the icons somebody had got round to copying, and the rest stayed inconsistent with it.

### One shape per icon

Twenty-one glyphs used to be assembled from several shapes each, so that one piece could move while
the rest held still — the two device LEDs blinking out of phase, the gear teeth turning around a
stationary hub, the diagnostics dot sweeping along its base, the sun's rays turning around a fixed
disc. A library gives one shape per name, so those parts are gone.

That was a deliberate trade, and it went this way: **one drawing system across the whole product
outranks segmented animation.** A single icon animating more richly than the rest is not worth having
one icon in the product that is not from the library. The motion was not dropped, it moved up a
level — each glyph now animates as a block, chosen to keep what the old segmentation meant:

| Destination | Was | Is |
| --- | --- | --- |
| Overview | inner dashboard bar pulsed | whole glyph breathes, 1.9 s |
| Devices | two LEDs blinked out of phase | whole glyph pulses on the same 1.4 s rack cadence |
| Administration | badge and check popped over a lifting base | whole glyph pops once |
| Diagnostics | a reading swept along the trace | whole glyph beats, 1.15 s |
| Settings | teeth turned around a stationary hub | whole cog turns, 7 s |
| Theme toggle | rays turned around a fixed disc | whole sun turns 45°, one ray pitch |

Amplitudes came down when the motion moved: a detail can travel a long way inside a silhouette that
holds still, while a whole icon moving that far reads as the row itself twitching.

The rules that follow: views reference semantic names only and never the library; `NutIconLibrary.cs`
is the single file permitted to know the library exists; nothing is fetched at runtime; and a name
added to the catalog without a mapping in the adapter is a defect, because it would draw from the
fallback and quietly leave one icon off the library. `IconCatalogTests` enforces all of it.

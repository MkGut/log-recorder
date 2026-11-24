# Fixed UI contract

Latest visual revision (November 9, 2026). The WinUI shell uses the more sculpted cobalt profile below, with a bowed shoulder reflection, a recessed display bevel, and separate sky-blue action and midnight-blue timer lenses.
Mode: Operate. One compact native Windows utility, not a dashboard. Sculpted organic Y2K media skin with cobalt-blue N64 molded plastic, deep-blue inset face and neo-Frutiger sky-blue lenses. The shape is an organic, molded media-player body; the casing color is cobalt-blue console plastic. No grey/silver casing or dense control layouts. This overrides all former square-corner guidance.

## Shared tokens — WinUI logical pixels/DIPs

| Token | Value |
|---|---|
| Window preferred width | 510; still accommodate 480 and 320 |
| Invitation preferred width | 395; still accommodate 360 |
| Minimum content width | 320; adapt rather than crop |
| Body inset | 24; 18 at narrow width |
| Spacing | 4, 8, 12, 16, 24 |
| Native chrome | Preserve supported drag/resize/close behavior; the visible molded header is specified below, not the retired 32-DIP square title strip |
| Curves | Asymmetric casing 106/34/94/36, or 72/27/62/30 at compact width. Inset 48/19/55/20, action lenses 24/10/24/10, timer lens 64/27/64/27. Values are DIPs clockwise from top left. |
| Casing / inset | Cobalt #294CE3 and #171FC2 with a sky-blue shoulder and blue return edge. The inset deepens from #132F7A to #060E32. |
| Main text | #EDF7FF on deep-blue inset |
| Secondary text | #BDD7FF |
| Border | Directional molded rim. The display bevel shades inward at the top and catches light along the bottom. |
| Header | Integral molded casing, 76 high; 74 × 64 sky-blue lens badge on the left and a 44-DIP close control on the right. A static curved reflection sits below the header. |
| Primary button | Sky-blue lens with a crisp reflection break between #65CEFA and #38A9EE and #06245E text |
| Secondary button | Deep-blue lens shading from #5279CB into #142860 with #EDF7FF text |
| Body typography | Segoe UI 13–14; secondary ≥12 |
| Heading | 24, regular/medium; 28 for goodbye |
| Timer | 70 at preferred width; 42–56 narrow; tabular digits |
| Button | minimum 94 wide, 36 high; 44-high coarse/touch targets |

The fixed blue-casing/deep-blue-inset material is intentionally selected by the latest user brief. Native Windows high-contrast colors override decorative gradients. Do not force light colors in high contrast. Support text scaling; dimensions are preferred, not excuses to clip. If native titlebar height/corners cannot match exactly, retain supported native behavior and report the difference.

One shared shell and one native button template for secondary, primary, and timer styles. Pointer-over and press change a translucent surface layer. Disabled buttons use a separate low-emphasis face. Keyboard focus uses the system focus visual. High contrast substitutes Windows colors and clears the decorative shoulder reflection. No glass shader, blur, animated decoration, image background, cards inside cards, giant source tiles, left navigation or fixed sidebar.

## Screen composition and exact main copy

- Invitation: title strip `log`; heading `Wanna lock in?`; short line `A little time for what matters.`; bottom-right Not now / Let's go. Never steals focus.
- Source: `Your time starts here.` The body explains that Windows asks which screen or window to share and recording starts after confirmation. Back / Choose source… opens the previous screen or the Windows picker, respectively.
- Recording: top-right `Recording · [actual source]`; large centered elapsed timer BUTTON; `Click the timer to finish`; quiet bottom line `Saved on this device. Close this window to keep going.` No preview.
- Goodbye: small `Still recording · [source]` and elapsed; `Sure you wanna say goodbye?`; `Keep this session as a timelapse.`; Keep going / Finish & preview. Still recording until explicit finish confirmation.
- Finishing: `Finishing your session…`; source-preservation status; real progress only when known, otherwise indeterminate. Mock's Simulate ready is TEST HARNESS ONLY.
- Preview: `A little time, kept.`; actual captured duration; small native player; Keep for later / Save timelapse. The mock's labeled player box is a placement guide, not footage to ship.
- Saved: `All yours.`; `Your timelapse is saved.`; real verified destination; Open file (only when supported) / Done.
- Consent cancelled: `No rush.`; `Nothing was recorded. Choose a source whenever you're ready.`; Not now / Choose source….
- Source lost: `Paused · source unavailable`; `Your session is safe.`; choose source again or Finish session. Only show preservation wording after actual durable state is established.
- Save error: `Couldn't save there.`; `Your recording is still on this device.`; Choose folder… / Keep for later. Never imply successful file save.

State owns available actions. It never owns the recording engine. Presentation shows actual durable/engine facts; where those facts are unknown, use truthful pending/error text instead of this fixture's happy-path copy.

## Focus and action rules

All real buttons support keyboard. Timer name: `Finish recording, elapsed [duration]`. Do not announce every timer tick to Narrator. On deliberate navigation focus the appropriate heading/action; notifications appearing do not take focus. Escape cancels goodbye back to recording; it never stops capture. Close while capturing hides to tray; close before saving retains unfinished output. Busy actions disable duplicates while keeping status accessible. Focus indicators are visible in every theme.

No screen is accepted until checked at 320 and 480 DIPs, 100/150/200% scaling, high contrast and keyboard-only. Visual mock is a reference, not evidence those checks passed.

## November 9 WinUI checkpoint

The refinement lives in `src/log.App/CobaltSurface.xaml` and its width adaptation in `CobaltSurface.xaml.cs`. The captionless WinUI host reads the same casing radii for its window outline. The separate lightweight shell uses a different implementation.

The isolated Fixture and Unpackaged x64 builds succeeded with zero warnings and errors. Native simulation showed the compact invitation, source selection, disabled actions during consent, the shaped timer, and the finish decision. Tab displayed a focus ring. Escape returned to recording with zero finish confirmations. The complete 100-hour timer fit at 320 DIPs at the current display scale. The long source name wrapped and ellipsized.

All 461 Release tests passed when run separately. The repository gate stopped at a file lock held by the running lightweight shell, before the native bridge and WinUI steps. The WinUI builds used isolated outputs. High contrast, the DPI matrix, and real capture/save remain outside this checkpoint's evidence.

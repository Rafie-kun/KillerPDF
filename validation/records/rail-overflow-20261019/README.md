# BF-L001 rail overflow acceptance

Passed 48 real WPF scenarios: Top/Bottom, open/collapsed, scales 0.7/1/1.5/2.5, and Dark/Light/98SE at 620x800. German is asserted through LocaleManager.Current. Synthetic PDFs exercise original controls, page jump, nested outlines and Comments edit.

The original 150% case clipped seven commands without overflow navigation. Additional exact failures exposed an offscreen outline action, a fullscreen strip gap, and an initially blank collapsed viewport. Each was reproduced before its corresponding repair.

Wide open/collapsed geometry remains 984x28 with zero differences for all 13 commands. Actual side-menu transitions preserve controls. Both overflow directions, focus scrolling, original flyout anchors, fullscreen/F11 restoration, heights 600/1200/800 and logo-wheel endpoints 0.7/2.5 pass.

- [Verdict, commands and hashes](result.json)
- [48 scenario verdicts](matrix.json)
- [Wide geometry comparison](wide-geometry.json)
- [Source provenance](source-provenance.json)
- [Verified screenshot hashes and dimensions](images.json)
- [Original clipping](red-top-150.png)
- [Collapsed 250% commands](green-top-250-collapsed.png)
- [Populated outline at 250%](green-top-250-outline.png)
- [Short window at 250%](green-top-250-short.png)
- [Light bottom rail at 150%](green-light-bottom-150.png)
- [98SE bottom rail at 250%](green-98se-bottom-250.png)
- [Wide baseline](wide-baseline.png) and [wide final candidate](wide-green.png)

Runner: `tests/KillerPDF.RailSmoke`. Arguments: `APP_DLL APP_XAML NEW_OUTPUT_DIRECTORY SIDE SCALE WIDTH THEME LOCALE open|collapsed --startup-smoke`. Output directories must be fresh; builds use scratch. Exact commands are in result.json.

Eight retained images were individually inspected at 96 DPI. Physical input, native monitor DPI, other themes/locales and multi-monitor behavior remain unverified. This does not certify the application or release.

Automatic approval review rejected deletion of an owned generated snapshot evidence copy and the verified owned RailSmoke obj directory. Both returned `blocked by policy`. Neither deletion was retried; the generated files remain. Future runner builds use scratch intermediates.

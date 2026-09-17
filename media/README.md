# Experiment recordings

Screen recordings of pseudo-3D conductivity imaging, captured from the workstation
during paired time-division acquisition. Each recording shows the conductivity change
reconstructed as the display-only pseudo-3D view, which quality-aware anisotropic
universal kriging builds across the two measured layers and reports together with a
relative variance. It uses only the two independent 2D parameter fields: no cross-layer
voltage is synthesised, and this is not a true 3D CEM inversion. It also carries the
inter-layer time difference of the time-division schedule.

All three runs start from a background solution at 11.4 µS/cm.

| File | Setup | What it shows |
| --- | --- | --- |
| `pseudo3d-acrylic-copper-cylinder.mp4` | Bucket of background solution | An acrylic rod and a hollow copper cylinder are placed into the solution. The two contrast against the background in opposite directions: the acrylic rod is an insulator, the copper cylinder a conductor. |
| `pseudo3d-kcl-single-droplet.mp4` | Bucket of background solution | A single droplet of 10 % w/w KCl solution enters the tank, and the conductivity change spreads from the entry point. |
| `pseudo3d-kcl-concentration-series.mp4` | Small bucket of background solution | Droplets of 2 %, 4 %, 6 %, 8 % and 10 % w/w KCl are added in sequence, so the response can be compared across an increasing concentration step. |

## Notes

- Conductivity values are not calibrated absolute readings; the imaging shows the
  change relative to the locked reference frame of each run.
- `pseudo3d-acrylic-copper-cylinder.mp4` is encoded with HEVC (H.265). Most browsers
  play H.264 but not HEVC, so this one may need a desktop player. The other two are
  H.264.
- These files are the recordings as captured. The experiment data behind them is not
  included in this repository.

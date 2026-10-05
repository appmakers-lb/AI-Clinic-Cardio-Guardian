# Third-party research model notice

Cardio Guardian can optionally load the **Stenoz coronary stenosis U-Net** research checkpoint
published by **uzbtrust** and trained on the ARCADE X-ray coronary angiography dataset.

- Upstream code/project: https://github.com/uzbtrust/stenoz
- Research checkpoint: https://huggingface.co/uzbtrust/stenoz-coronary-stenosis-unet
- Upstream code license: MIT
- Cardio Guardian does **not** redistribute the checkpoint in this repository; the setup script
  downloads it into the ignored local `ai-research/models/` folder.

The upstream project explicitly describes the model as research/demonstration software and not a
clinically validated or certified diagnostic system. Its published operating point prioritizes
recall and has substantial false positives. Cardio Guardian therefore labels every output as a
**research stenosis candidate**, does not infer severity or treatment, and never treats a negative
result as clinical clearance.

Copyright (c) 2026 uzbtrust. Used under the MIT license.

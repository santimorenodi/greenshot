Greenshot - a free screenshot tool optimized for productivity
=============================================================

Welcome to the source repository for Greenshot.

What is Greenshot?
------------------

Greenshot is a light-weight screenshot software tool for Windows with the following key features:

* Quickly create screenshots of a selected region, window or fullscreen.
* Easily annotate, highlight or obfuscate parts of the screenshot.
* Export the screenshot in various ways: save to file, send to printer, copy to clipboard, attach to e-mail, send Office programs or upload to photo sites like Flickr or Picasa, and others.
and a lot more options simplifying creation of and work with screenshots every day.

Being easy to understand and configurable, Greenshot is an efficient tool for project managers, software developers, technical writers, testers and anyone else creating screenshots.


[If you find that Greenshot saves you a lot of time and/or money, you are very welcome to support the development of this screenshot software.](https://getgreenshot.org/support/)

Trademark and Logo Usage Policy
-------------------------------

The Greenshot logo and trademark are the property of the Greenshot development team. Unauthorized use of the logo and trademark is generally prohibited. However, we allow the use of the Greenshot name and logo in the following contexts:

* In blog posts, articles, or reviews that discuss or promote the Greenshot, provided that the usage is fair and does not imply endorsement by Greenshot.
* In educational materials or presentations that accurately represent the project.

Please refrain from using the Greenshot logo and trademark in any promotional materials, products, or in a manner that may cause confusion or imply endorsement without prior written permission.

If you have any questions or wish to seek permission for other uses, please contact us.

Thank you for your understanding and cooperation.


About this repository
---------------------
This is the ongoing development branch for future Greenshot releases. 

Releases
--------

You can find a list of all releases (stable and unstable) in the [Github releases](https://github.com/greenshot/greenshot/releases) or in the [version history on our website](https://getgreenshot.org/version-history/).
The [downloads page on our website](https://getgreenshot.org/downloads/) always links to the latest stable release.

Command line screenshots (greenshot-cli)
----------------------------------------

Building the solution also produces `greenshot-cli.exe` next to `Greenshot.exe`. It takes screenshots with the Greenshot
capture engine without any UI, uses default settings in memory (never touches greenshot.ini) and can run while Greenshot is running.

    greenshot-cli list                                    monitors (index) and windows (handle, title)
    greenshot-cli capture [target] [options]

    target:   --fullscreen (default) | --monitor N | --active | --window 0xHANDLE | --window "title" | --region X,Y,W,H
    options:  -o FILE (png/jpg/bmp/gif/tiff from extension), --format F, --quality N, --delay SEC,
              --mode auto|aero|aerotransparent|gdi|screen, --clipboard, --open (open in Greenshot editor)

It prints `saved: PATH` and `size: WxH`, exit code is 0 on success.

It also annotates, with the Greenshot editor itself running headless: each element is drawn as if dragged with the
mouse, so the result looks exactly like annotating in the editor. Annotations go after `capture`, or on any image with
`edit`, and are applied in the order given:

    greenshot-cli edit INPUT [-o FILE] [annotations]      png, jpg, bmp, gif, tiff or .greenshot in, same out

    elements: --rect/--ellipse X,Y,W,H, --line/--arrow X1,Y1,X2,Y2, --freehand "X,Y;X,Y;...",
              --text X,Y[,W,H] TEXT, --bubble X,Y,W,H,TX,TY TEXT, --step X,Y[,SIZE] (numbered 1, 2, 3...),
              --highlight, --spotlight, --grayscale-area, --magnify, --pixelate, --blur X,Y,W,H, --crop X,Y,W,H
    style:    --color, --fill (name, #RRGGBB, #AARRGGBB), --thickness, --font, --font-size, --bold, --italic,
              --shadow/--no-shadow, --heads end|start|both|none, --pixel-size, --blur-radius, --magnification
    effects:  --border, --drop-shadow, --torn-edge, --grayscale, --invert, --rotate N, --resize W,H, --scale PERCENT

Style arguments apply to the elements after them. Saving to `.greenshot` keeps every element editable in Greenshot.

    greenshot-cli capture --active --crop 0,0,1280,760 --pixelate 860,20,300,32 --color #E53935 --thickness 4 ^
      --arrow 700,420,560,300 --step 700,430 --font-size 20 --bubble 760,460,300,70,700,430 "Then press Save" ^
      --drop-shadow -o howto.png

Default hotkeys in this fork: region `PrintScreen`, window `Alt + PrintScreen`, last region `Shift + PrintScreen`,
fullscreen `Ctrl + Alt + PrintScreen` (moved from `Ctrl + PrintScreen`, which is used by wcap).

Claude Code plugin
------------------

This repo is also a [Claude Code](https://claude.com/claude-code) plugin marketplace. The `greenshot` plugin adds skills
that teach Claude to take screenshots with `greenshot-cli` (`take-screenshot`), to annotate them (`annotate-screenshot`)
and to build & configure Greenshot (`setup-greenshot`):

    /plugin marketplace add santimorenodi/greenshot
    /plugin install greenshot@greenshot

The plugin only contains instructions; `greenshot-cli.exe` must be built (see `setup-greenshot`) and be on `PATH`,
in `GREENSHOT_CLI`, or in `src/Greenshot/bin/Release/net480/`.

Getting Started for Developers:
-------------------------------

These instructions are made to assist developers in getting started with Greenshot so they can contribute to the repository. Please verify system prerequisites are met before trying to build.

IDE System Requirements:
------------------------

* Windows OS environment
* Greenshot is build using (as of this writing) .net Framework 4.8.0 This means any version between .net Framework 4.8.0-4.8.1 will suffice. 
* Visual Studio 2022 or newer (works fine with 2026)
* .NET SDK 9.0.311 also for building (supported by VS 2022)

Build Instructions:
-------------------

* Open Visual Studio 2022 or 2026
* Clone GitHub Repository using Visual Studio, using the link in the green code button above. Alternatively, you can download the repository to your machine and open the solution file located in /src/Greenshot.sln.
* Choose Build->Build Solution in Visual Studio to build binaries.
* Verify all components are built successfully.
* You are ready to start contributing to Greenshot.

How to contribute:
------------------

* Create your own fork of the main repository
* Make desired changes (REMEMBER: keep commits small and concise, don't try to add multiple separate changes at once)
* Submit a pull request for review. Your request will be reviewed and either accepted, denied, or you may be asked to make revisions before your code is accepted.

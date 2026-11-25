# Flow

## Platform
adaptive

## Users

People who want to dictate into the application they are already using. The confirmed priority for this work is global dictation on Windows.

## Product Purpose

Flow turns speech into text that can be inserted into the active application. It also lets users import and review meeting recordings.

## Positioning

Flow is local-first. Windows and Android can use local speech and text models; Groq is an optional hosted provider. FlowHub is an optional server for synchronizing selected data between devices.

## Operating Context

On Windows, Flow can stay resident, start dictation with Ctrl+Win, and show recording status without taking focus from the active application. The Android client has a Quick Settings tile and an optional floating control.

## Capabilities and Constraints

- Dictation can be inserted into the currently focused application.
- Audio recognition and text post-processing providers can be selected independently on Windows.
- Whisper local and Ollama local are available after their model/runtime requirements are met; Groq requires an API key.
- Meeting recordings can be imported, transcribed, reviewed, and exported.
- FlowHub is optional. Device-specific settings, including model-provider choices, stay local.
- The redesign must preserve the current Windows features, controls, bindings, keyboard shortcuts, and recording behavior.

## Brand Commitments

- Interface copy is functional and avoids slogans, as requested by the user.
- The Windows interface is being redesigned around square modules and restrained ASCII details, as requested by the user.
- The recording bubble takes motion inspiration from Voice Island: an expandable black capsule, a square stop control, two strands of green and lime light particles responsive to audio level, a timer, and live words beneath it. The animation is rendered natively in WPF.

## Evidence on Hand

- `README.md`
- `Flow.Windows/README.md`
- `Flow.Android/README.md`
- `protocol.md`
- Current Windows XAML and code-behind
- User confirmation in the redesign request: global Windows dictation is the primary use; preserve functionality and controls.

## Product Principles

- Make dictation available from the user's current application with as few steps as possible.
- Keep local processing and optional synchronization understandable in the interface.
- Show recording and processing state without disrupting the active task.

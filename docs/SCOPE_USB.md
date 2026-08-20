# Connecting an oscilloscope over USB

The scope panel reads **ASCII samples from a serial port**. That covers a USB-serial scope,
a bench scope with a serial/VCP mode, and — most usefully in practice — any microcontroller
acting as a front end. No driver, SCPI stack or vendor SDK is involved.

## Wire format

One line per **sample set**. Every numeric token on the line is one channel, in order:

```
1.234                 -> ch1
0.10,0.42             -> ch1, ch2
0.10 0.42 -0.05       -> ch1, ch2, ch3
t=1024 v1=1.2 v2=0.4  -> ch1=1024, ch2=1.2, ch3=0.4   (see note)
```

- Values are **volts** (whatever scale you feed it becomes the Y axis; `Volts / division`
  scales the view).
- Up to **4 channels**. The channel count latches from the lines as they arrive and is shown
  in the PCB… sorry, in the **Oscilloscope** tab as `N ch @ M Hz`.
- Lines are separated by `\n` (a trailing `\r` is harmless).
- Anything non-numeric is skipped, so `#`-comments and units are tolerated — but note the
  fourth example above: a timestamp column becomes *channel 1*. Send only the channels you
  want plotted, or disable channel 1 in the tab.

Sample rate is measured, not configured: whatever rate you send at is what the frequency and
period measurements are computed against. Send at a steady rate for accurate numbers.

## Minimal Arduino / RP2040 front end

```cpp
// Two channels, ~2 kHz, 12-bit ADC scaled to volts.
void setup() { Serial.begin(115200); }

void loop() {
  static uint32_t next = 0;
  if (micros() < next) return;
  next = micros() + 500;                      // 2 kHz

  float v1 = analogRead(A0) * 3.3f / 4095.0f;
  float v2 = analogRead(A1) * 3.3f / 4095.0f;
  Serial.print(v1, 4); Serial.print(','); Serial.println(v2, 4);
}
```

At 115200 baud an 14-byte line costs ~1.2 ms, so ~800 lines/s is the practical ceiling.
For faster capture raise the baud rate (1 Mbaud works on USB CDC, where the "baud" is
nominal) or send fewer digits.

## Setting it up in EDes

1. Plug the device in; open the **Oscilloscope** tab.
2. The panel lists detected ports live (`Ports: COM3, COM7`). Type one into **Serial port**
   or press **Next detected port**.
3. Set **Baud** to match the device.
4. Tick **Read from USB serial**.

Status shows `USB COM7 @ 115200` once open. If the device is absent or busy, the port is
retried every 2 seconds and the status says so — nothing blocks, and the panel falls back to
the synthetic waveform so the display never goes dead. Untick the toggle to go back to
synthetic on purpose.

## Controls (in the volume)

| Key | Action |
|---|---|
| `Tab` | switch to/from full-screen scope mode |
| `1`–`4` | toggle channels |
| `Up` / `Down` | volts per division |
| `Left` / `Right` | trigger level |
| `T` | trigger channel (cycles, then free-run) |
| `E` | trigger edge (rising / falling) |
| `P` | freeze acquisition |

## Triggering

The trigger is a software edge search over the captured window: the most recent crossing of
the trigger level is found and the window is aligned so that edge sits 20% in from the left.
Without it, a periodic waveform slides sideways every frame and is unreadable. Set the
trigger channel to `-1` (or press `T` past channel 4) for free-run.

## Measurements

Computed per channel, per frame, on the snapshot: **Vpp**, **Vrms**, **Vmin/Vmax/Vmean**,
**frequency** and **period** (from rising zero-crossings of the mean-removed signal), and
**duty cycle** above the mean. Frequency needs at least two crossings in the window — if the
signal is slower than the window, widen the window by lowering the sample rate, or read the
period off the graticule.

A trace that clips the top or bottom of the face turns **red** at the clipped samples, so an
overdriven input is obvious rather than looking like a flat-topped waveform.

## Where the panel lives

The scope face is drawn on a single constant-Y plane — `Readout plane Y`, default **0.1** —
and is deliberately **not** rotated by the scene camera. It stays readable while you fly the
circuit or board around it, exactly like a bench scope bolted to the bench. In Education mode
it occupies a strip at the bottom of the volume beneath the circuit; in Scope mode it fills
the volume.

One sample is drawn per voxel column across the face, so the trace is never over- or
under-sampled for the display size: the window length adapts to the display, not the other
way round.

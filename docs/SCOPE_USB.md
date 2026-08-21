# Connecting an oscilloscope

EDes reads waveforms two ways, chosen with **Source** in the Oscilloscope tab:

| Source | For | Needs |
|---|---|---|
| **SCPI over TCP** | a bench instrument with an Ethernet port (Rigol, Siglent, Keysight, R&S) | nothing — a raw socket, no driver, no vendor software |
| **SCPI over USBTMC** | the same instrument over its USB Device port | an installed VISA runtime (that install is also what binds the USBTMC driver) |
| **Serial** | an MCU front end, a logger, a scope in VCP mode | a COM port |
| **Synthetic** | demos and development | nothing |

Jump to [Bench instruments over SCPI](#bench-instruments-over-scpi) for the first two.

---

## Serial: ASCII sample streams

The serial source reads **ASCII samples from a serial port**. That covers a USB-serial scope,
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
  in the **Oscilloscope** tab as `N ch @ M Hz`.
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


---

# Bench instruments over SCPI

Tested command set: **Rigol DS2000/MSO2000** (an MSO2302A is the reference instrument),
which is also what DS1000Z, DS4000, MSO5000 and most Siglent scopes accept — the
`:WAVeform` subsystem is near-identical across all of them.

## Which transport

**Use Ethernet if the instrument has a port.** A raw SCPI socket needs no driver, no VISA,
no vendor software; you can telnet to it to debug; and it survives reboots of everything.
USBTMC works, but only once a VISA runtime is installed.

### Ethernet (SCPI over TCP)

1. On the scope: **Utility → IO Setting → LAN Conf.** Set **DHCP** *or* a **static** IP —
   see the warning below about link-local addresses.
2. Note the IP the scope displays.
3. In EDes: Oscilloscope tab → **Source: SCPI over TCP**, put the IP in **Instrument IP**,
   leave **TCP port** at **5555** (Rigol's raw socket port).

> **A `169.254.x.x` address means the LAN is NOT ready.** That is a link-local
> self-assignment, which happens when DHCP gets no answer — e.g. the scope is plugged
> straight into a PC, or into a switch with no DHCP server. Rigol scopes do **not** start
> their SCPI socket service in that state: the instrument pings but every port is closed.
> Fix it one of two ways:
>
> - **Put it on your router** (easiest): plug the scope into the same LAN as the PC and let
>   DHCP give it a real address, e.g. `192.168.1.x`.
> - **Set a static IP** on the scope: `Utility → IO Setting → LAN Conf.`, turn **DHCP off**
>   and **Auto IP off**, then set e.g. IP `192.168.1.50`, mask `255.255.255.0`, gateway
>   `192.168.1.1` — and **Apply**. If the scope is wired directly to the PC, give the PC's
>   Ethernet adapter a static address in the same subnet.
>
> Also check **Utility → IO Setting → RemoteIO** and make sure **LAN** is enabled there.

Verify from the command line before blaming the app:

```bash
ping 192.168.1.50
```

### USB (SCPI over USBTMC)

The MSO2302A's USB Device port is USBTMC, not a serial port — Windows shows it as an
unknown device (status **Error**) until a VISA runtime is installed. Install **one** of:

- **NI-VISA** (free) — the most widely used, includes VISA.NET and the USBTMC driver
- **Keysight IO Libraries Suite** (free)
- **Rigol Ultra Sigma** — bundles Rigol's USBTMC driver
- **R&S VISA** (free)

Then: Oscilloscope tab → **Source: SCPI over USBTMC**. Leave **VISA resource** blank to take
the first USB instrument, or press **Next VISA resource** to cycle what the runtime can see
(e.g. `USB0::0x1AB1::0x04B0::DS2Axxxxxxxx::INSTR`). The panel reports
`VISA runtime: present / NOT installed`, so you can tell a missing runtime from a missing
instrument.

EDes loads `visa32.dll` dynamically, exactly like the Voxon SDK DLLs — no VISA means a clear
message, not a crash at start-up.

## What EDes asks the instrument for

Per channel, per acquisition:

```
:WAV:SOUR CHAN<n>     :WAV:MODE NORM     :WAV:FORM BYTE
:WAV:PRE?             10 CSV fields: format,type,points,count,
                      xincrement,xorigin,xreference,yincrement,yorigin,yreference
:WAV:DATA?            #<n><length><bytes>
```

and converts with the formula these scopes document:

```
volts          = (raw - yorigin - yreference) * yincrement
sample rate Hz = 1 / xincrement
```

**NORMal mode** (screen memory, ~1400 points) is used rather than **RAW** (deep memory, up to
14 Mpts) on purpose: NORMal reads while the scope keeps running, is already decimated to
something a display can show, and costs one transfer per channel. RAW would require stopping
the scope and chunking the read, for detail the volume cannot resolve.

Enabled channels are re-queried (`:CHAN<n>:DISP?`) about every two seconds, so switching a
channel on at the front panel appears without reconnecting. **SCPI acquisitions / second**
sets the update rate; 10/s is a good default, and the scope's own trigger means each window
arrives already stable.

The sample rate used for frequency and period measurements is the one the instrument
**declares** (`1 / xincrement`), not the arrival rate of the blocks — a 1400-point window
landing 10x a second is not a 14 kHz stream.

## Troubleshooting

| Symptom | Cause |
|---|---|
| `No answer from <ip>:5555` | LAN service not running — see the link-local warning above |
| Instrument pings but all ports closed | same |
| `No VISA runtime installed` | install NI-VISA or Ultra Sigma, then reselect the source |
| `No USB instrument visible to VISA` | runtime installed but the driver is not bound — reinstall/replug, check Device Manager |
| Connects, but the trace is flat | channel is off on the scope, or the probe is on nothing; `:CHAN1:DISP?` is what EDes trusts |
| `SCPI error: TimeoutException` | the instrument is busy (deep-memory acquisition, or a long single-shot); lower the acquisition rate |

# External component notices

The project's managed source, tests, examples and documentation use
Apache-2.0, Copyright 2026 Marz. See LICENSE.txt, LICENSE-SCOPE.md and NOTICE.

## 7-Zip native components supplied by the caller

This repository does not include `7z.dll`, SFX modules, a 7-Zip executable,
installer or native SDK source. Callers supply the components needed at runtime.
The existing integration baseline uses 7-Zip 25.01.

The corresponding upstream license is retained as an unmodified reference in
[licenses/7zip-License.txt](licenses/7zip-License.txt). External native components
have their own applicable LGPL, BSD and unRAR terms. The bridge's Apache license
does not replace those terms or authorize native binary redistribution.
Consumers distributing a native payload must retain its applicable notices and
satisfy its upstream requirements.


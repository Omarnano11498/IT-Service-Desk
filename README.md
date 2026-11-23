# نظام إدارة تذاكر الدعم الفني (IT Service Desk) — WPF + SQL Server

تطبيق سطح مكتب بلغة C# (.NET 8 / WPF) مع قاعدة بيانات Microsoft SQL Server لاستقبال طلبات الدعم الفني وتنظيمها وجدولتها ومتابعتها حتى الحل، مع لوحة إحصاءات ونظام لتقييم جودة الخدمة.

- **وثيقة التصميم الكاملة** (المعمارية، ERD، التعقيد الخوارزمي، المعادلات): [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)
- **سكربت قاعدة البيانات:** [`database/ServiceDesk_Setup.sql`](database/ServiceDesk_Setup.sql)

---

## 1. المتطلبات

| المكوّن | الإصدار |
|---|---|
| نظام التشغيل | Windows 10 / 11 (WPF و DPAPI خاصّان بـ Windows) |
| .NET SDK | 8.0 أو أحدث |
| بيئة التطوير | Visual Studio 2022 (17.8+) مع حمولة ".NET desktop development"، أو `dotnet` CLI |
| SQL Server | 2016+ / Express / Developer / LocalDB / Azure SQL |
| أدوات اختيارية | SSMS أو Azure Data Studio أو `sqlcmd` |

حزم NuGet (تُستعاد تلقائيًا): `Microsoft.Data.SqlClient 5.2.2`، `System.Security.Cryptography.ProtectedData 8.0.0`، ولمشروع الاختبارات `xunit`.

---

## 2. خطوات الإعداد والتشغيل

### الخطوة 1 — إنشاء قاعدة البيانات

> ⚠️ السكربت يحذف قاعدة `ServiceDesk` إن وُجدت ثم يعيد إنشاءها.

**على الجهاز نفسه (Localhost):**
```bat
:: مثيل افتراضي
sqlcmd -S localhost -E -C -i database\ServiceDesk_Setup.sql
:: SQL Express
sqlcmd -S .\SQLEXPRESS -E -C -i database\ServiceDesk_Setup.sql
:: LocalDB
sqlcmd -S "(localdb)\MSSQLLocalDB" -E -i database\ServiceDesk_Setup.sql
```

**على خادم بعيد (عنوان IP):**
```bat
sqlcmd -S 192.168.1.50,1433 -U sa -P "<كلمة المرور>" -C -i database\ServiceDesk_Setup.sql
```

أو افتح الملف في SSMS واضغط **Execute**.

### الخطوة 2 — (للاتصال عبر IP فقط) تجهيز الخادم البعيد

1. **SQL Server Configuration Manager** ← SQL Server Network Configuration ← فعّل **TCP/IP**، وثبّت المنفذ 1433 في IPAll، ثم أعد تشغيل الخدمة.
2. افتح المنفذ في جدار الحماية على الخادم:
   ```powershell
   New-NetFirewallRule -DisplayName "SQL Server 1433" -Direction Inbound -Protocol TCP -LocalPort 1433 -Action Allow
   ```
3. لاستخدام **SQL Authentication**: فعّل الوضع المختلط (Mixed Mode) من خصائص الخادم في SSMS، ويُفضّل إنشاء مستخدم بأقل صلاحيات (القسم 9 في نهاية السكربت).
4. لاستخدام اسم مثيل دون منفذ: شغّل خدمة **SQL Server Browser** وافتح UDP 1434.

### الخطوة 3 — البناء والتشغيل

```bat
dotnet restore ServiceDesk.sln
dotnet build ServiceDesk.sln -c Release
dotnet run --project src\ServiceDesk.Wpf
dotnet test tests\ServiceDesk.Tests
```
أو افتح `ServiceDesk.sln` في Visual Studio، واجعل `ServiceDesk.Wpf` مشروع البدء، ثم اضغط F5.

### الخطوة 4 — أول تشغيل

1. تُفتح شاشة **Settings ← SQL Server connection** تلقائيًا (لا توجد إعدادات محفوظة بعد).
2. اختر **Localhost** (ثم نوع المثيل) أو **Remote IP address** (العنوان + المنفذ)، ثم اسم القاعدة وطريقة المصادقة.
3. اضغط **Test connection** ← يجب أن تظهر رسالة "Schema is ready".
4. اضغط **Save & apply** (تُحفظ الإعدادات في `%AppData%\ServiceDesk\connection.json`، وكلمة مرور SQL مشفّرة بـ DPAPI).

> بعد الحفظ تصبح الإعدادات متاحة للمسؤول فقط.

### الخطوة 5 — الدخول كمسؤول

| الحقل | القيمة الافتراضية |
|---|---|
| Username | `admin` |
| Password | `Admin@12345` |

1. **غيّر كلمة المرور فورًا:** Settings ← My password.
2. افتح **Statistics & evaluation** واضغط **Recalculate evaluations** مرة واحدة لتقييم التذاكر التجريبية المحلولة.

---

## 3. دليل الاستخدام المختصر

### وضع المستخدم النهائي (دون كلمة مرور)
- **Submit a ticket:** البيانات الشخصية، والفئة (تظهر حقول خاصة بالأجهزة أو البرمجيات أو الشبكات)، ودرجة الإلحاح والتأثير مع معاينة فورية للأولوية الناتجة ← يحصل المستخدم على رقم مثل `TKT-0000010`.
- **Track my ticket:** إدخال الرقم + البريد معًا ← الحالة والـ SLA والتحديثات العامة وملاحظات الحل، مع تقييم الرضا (1–5) بعد الحل.

### وضع مسؤول تقنية المعلومات (بكلمة مرور)
- **Queue & triage:** طابور مرتب حسب الاستراتيجية المختارة، ومرشحات (الحالة، الأولوية، الفئة، البحث، تذاكري فقط)، وتمييز لوني للتذاكر المتجاوزة والمعرّضة للخطر، وتحديث تلقائي كل 30 ثانية.
  - **Receive next ticket:** يسحب أعلى تذكرة غير مُسندة من الكومة ويسندها إليك.
  - الإجراءات: Claim، Start work، Pending، Escalate، Return to queue، Change priority، Add note (داخلية أو عامة)، Resolve & evaluate، Close، Reopen. الأزرار غير المسموحة في الحالة الحالية تُعطَّل تلقائيًا.
  - سجل تدقيق كامل غير قابل للتعديل لكل تذكرة.
- **Statistics & evaluation:** مؤشرات الأداء، وزمن الحل (المتوسط، الوسيط، الحد الأدنى، الحد الأقصى، P90، الانحراف المعياري)، ونسب الالتزام بالـ SLA، والتوزيعات، وتقييم كل تذكرة، والتصدير إلى CSV و JSON.
- **Settings:** الاتصال بـ SQL، وجدول سياسات الـ SLA (قابل للتعديل)، وتغيير كلمة المرور.

---

## 4. هيكل الحل

```
ServiceDesk.sln
├── database/ServiceDesk_Setup.sql        DDL + فهارس + مشغّل منع التعديل + بيانات أولية
├── docs/ARCHITECTURE.md                  وثيقة التصميم
├── src/
│   ├── ServiceDesk.Domain/               الكيانات، القواعد، الاستراتيجيات، المصنع، الخوارزميات
│   ├── ServiceDesk.Application/          الخدمات، الواجهات المجردة، التجزئة، الإحصاء، التصدير
│   ├── ServiceDesk.Data/                 ADO.NET، المستودعات، UoW، الإعدادات (Singleton)
│   └── ServiceDesk.Wpf/                  Views، ViewModels، Converters، Styles، جذر التركيب
└── tests/ServiceDesk.Tests/              اختبارات xUnit للمجال والخوارزميات والمعادلات
```

---

## 5. ميزات مقترحة للمرحلة التالية

| الميزة | القيمة | ملاحظة تنفيذية |
|---|---|---|
| إيقاف ساعة الـ SLA أثناء `Pending` + تقويم ساعات العمل والعطلات | قياس أعدل | جدول `BusinessCalendars` وحساب المدة الصافية |
| إشعارات بالبريد (SMTP / Microsoft Graph) | إبلاغ المستخدم دون فتح التطبيق | Outbox Pattern لضمان الإرسال |
| المرفقات (لقطات شاشة، سجلات) | تشخيص أسرع | `VARBINARY(MAX)` + FILESTREAM أو تخزين خارجي |
| فرق دعم متعددة وتوجيه تلقائي حسب الفئة | توزيع حِمل | جدولا `Teams` و`CategoryRouting` |
| قاعدة معرفة (Known Errors) | حل ذاتي وتقليل التذاكر | ربط `RootCause` بمقالات |
| Full-Text Search | بحث O(log N) بدل المسح | `CREATE FULLTEXT INDEX` |
| تسجيل الأحداث (Serilog) وحاوية DI | تشغيل ومراقبة أسهل | `Microsoft.Extensions.Hosting` |
| واجهة عربية RTL كاملة | ملاءمة المستخدمين | ملفات `.resx` و`FlowDirection="RightToLeft"` |
| قفل الحساب في قاعدة البيانات بدل الذاكرة | حماية تصمد بعد إعادة التشغيل | عمودا `FailedLogins` و`LockoutUntil` |

---

## 6. استكشاف الأخطاء

| العَرَض | السبب المرجّح | الحل |
|---|---|---|
| خطأ 53 / Server not reachable | TCP/IP معطّل أو جدار الحماية | الخطوة 2 |
| خطأ 18456 / Login failed | بيانات خاطئة أو الوضع المختلط غير مفعّل | فعّل Mixed Mode أو استخدم Windows Auth |
| خطأ في الشهادة (certificate chain) | Encrypt=Mandatory مع شهادة ذاتية التوقيع | فعّل "Trust server certificate" (للتطوير) |
| "ServiceDesk tables were not found" | لم يُنفَّذ السكربت على القاعدة المختارة | الخطوة 1 |
| "modified by another administrator" | تعارض تزامن مقصود | اضغط Refresh ثم أعد المحاولة |

# Steam Card Collector 0.2.0

Windows x64 için basit C# / WPF kart toplama uygulaması.

## Bu sürümde

- Oynanıyor durumu bilgisayarda açık olan Steam istemcisiyle, Steamworks.NET üzerinden oluşturulur. Her oyun ayrı ve gizli bir alt süreçte çalışır. Gerçek oyun dosyaları açılmaz.
- SteamKit2 yalnızca web girişi, oyun kütüphanesi ve rozet sayfalarındaki kart haklarını okumak için kullanılır. SteamKit2 üzerinden `ClientGamesPlayed` gönderilmez.
- Yerel Steam ID ile web Steam ID eşleşmeden toplama başlamaz. Arayüz iki kimliği ve eşleşme durumunu gösterir.
- Yerel hesap, çalışan Steam sürecinin `ActiveProcess` kaydından okunur. `loginusers.vdf` içindeki son kullanılan hesap güncel oturum kanıtı sayılmaz. Alt süreç, bağlantı öncesi bu kimliği; bağlantı sonrasında Steamworks'ün gerçek Steam ID, App ID, çevrimiçi oturum ve sahiplik sonuçlarını kontrol eder.
- Yerel Steam kapanır, yeniden başlar veya hesap değişirse toplama durur. Alt süreç kimliği 50 ms döngüsünde, ana uygulama 500 ms aralıkla denetler. Bunlar atomik Steam hesap kilidi değildir; bağlantı kurulurken hesap değişirse süreç kapatılır ve başarılı başlangıç bildirilmez.
- Durdur, pencereyi kapatma ve hata durumunda alt süreç kapatılır ve `SteamAPI.Shutdown` çağrılır. Ana uygulama çökerse iletişim kanalının kapanması veya en fazla 5 saniyelik kalp atışı süresi alt süreci sonlandırır. Yanıt vermeyen süreç zorla kapatılır. Steam arayüzü durumu gecikmeli güncelleyebilir.
- Tek oyunun eksik, bilinmeyen veya geçersiz kart sayacı diğer doğrulanmış oyunları engellemez. Okunamayan oyunlar sıfır sayılmaz. Yalnızca okunamayan oyunlar kaldığında tüm kartlar tamamlandı denmez.

Yerel bağlantı ve süreç ayırma yaklaşımı için [SteamAchievementUnlocker](https://github.com/Rejixter/SteamAchievementUnlocker) incelendi. Steamworks.NET 20.2.0 ve aynı SDK 1.57 x64 DLL kullanılır. Başarım değiştirme kodu kullanılmaz. ASF motoru kullanılmaz.

## Kullanım

1. Windows ZIP'inin tamamını bir klasöre çıkarın. `lib` ve diğer dosyalar EXE yanında kalmalı.
2. Steam'i aynı Windows kullanıcısında açıp çevrimiçi giriş yapın. Uygulamayı Steam'den farklı yönetici yetkisiyle açmayın.
3. `SteamCardCollector.App.exe` dosyasını açın. Ek .NET kurulumu veya terminal gerekmez.
4. Kart haklarını okumak için aynı Steam hesabının kullanıcı adı ve şifresiyle **Web'e bağlan** düğmesine basın. Steam Guard kodunu girin veya telefonunuzdan onaylayın. Şifre ve oturum anahtarları diske kaydedilmez.
5. Yerel ve web Steam ID değerlerinin eşleştiğini kontrol edip **Başlat** düğmesine basın. Otomatik başlat varsayılan olarak kapalıdır; açılırsa aynı kimlik kontrolleri uygulanır.
6. **Durdur** ile durdurun. Yenile, kart haklarını yeniden okur. Tüm oyunlar sekmesi web hesabının kütüphanesini gösterir.

Steam kapalıyken veya hesaplar farklıyken web kart hakları yine okunabilir; toplama engellenir. Kütüphane girişte yüklenir; sonradan alınan oyunlar için web hesabına yeniden bağlanın.

Kart hakları üç dakikada bir kontrol edilir. Oyunlar tek tek işlenir. Steam'in HTML yapısı değişirse okuyucu güncellenmelidir. Giriş sayfası, yanlış web kimliği veya tüm sayfayı etkileyen hata sıfır kart hakkı sayılmaz ve toplamayı durdurur. Sahiplik veya yerel bağlantı hatası da toplamayı durdurur.

## Doğrulama sınırı

0.1.2'de kullanıcı tarafından gerçek hesap girişi, Steam Guard ve kart hakkı listesi doğrulanmıştı. 0.2.0'da ağ/hesap kullanmayan otomatik kontroller, WPF açılışı, native DLL yüklemesi ve farklı hesaplı alt süreç isteğinin reddedilmesi kontrol edilir.

**Yeni yerel oynanıyor akışı ve gerçek kart düşüşü gerçek hesapta uçtan uca doğrulanmadı.** Derleme ve test başarısı kart düşüşü kanıtı değildir. Steam kart düşürmesini garanti etmez.

Gerçek hesapta kontrol: Steam'de oynanıyor görünmesi; Durdur sonrası durumun kalkması; kart hakkı bitince sıradaki oyuna geçmesi; Steam'i kapatma/hesap değiştirme sırasında durması; web hesabı farklıyken Başlat'ın engellenmesi.

## Geliştirme ve paketleme

Windows, .NET 8 SDK veya daha üstü gerekir. Uygulama x64'tür. Native DLL kaynak ZIP'inde `lib/steam_api64.dll` olarak bulunur; SHA-256 değeri yüklemeden önce kontrol edilir. .NET 8.0.33 runtime Windows paketine dahildir.

```powershell
dotnet build SteamCardCollector.sln -c Release
dotnet run --project tests/SteamCardCollector.Checks -c Release
dotnet run --project src/SteamCardCollector.App -c Release
powershell -ExecutionPolicy Bypass -File scripts/Publish-Release.ps1
```

Paket betiği kontrolleri çalıştırır, Windows x64 self-contained uygulamayı yayımlar, native/WPF/alt süreç kontrollerini yürütür ve `dist/0.2.0` altına Windows ve kaynak kodu için ayrı ZIP'ler ile SHA-256 özetlerini yazar. Kaynak ZIP'i `.git`, `.codex`, derleme önbellekleri veya kullanıcı giriş bilgilerini içermez.

- `src/SteamCardCollector.App`: basit WPF arayüzü ve alt süreç giriş noktası.
- `src/SteamCardCollector.Core`: kart kuyruğu ve bağlantı arayüzleri.
- `src/SteamCardCollector.Steam`: web girişi, rozet okuyucu, yerel kimlik ve Steamworks alt süreç yönetimi.
- `tests/SteamCardCollector.Checks`: kuyruk, HTML, kimlik eşleşmesi ve hesap değişimi kontrolleri.
- `ThirdParty`: bağımlılık lisansları ve SteamKit2'nin karşılık gelen kaynak arşivi.

Bağımlılık açıklamaları: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## GitHub'da otomatik EXE paketi

`.github/workflows/release.yml`, Windows üzerinde testleri ve paket betiğini çalıştırır.
`main` / `master` commit'leri ve pull request'ler için paket Actions sayfasındaki
çalıştırmanın Artifacts bölümünden indirilir. Bir Release veya Pre-release
**yayımlandığında**, Windows ZIP'i, kaynak ZIP'i ve `SHA256SUMS.txt` aynı Release'in
Assets bölümüne otomatik eklenir. Taslak kaydetmek yayını tetiklemez.

1. Kaynak ZIP'inin içindeki `.github`, `src`, `tests`, `scripts`, `lib`, `ThirdParty`
   klasörlerini ve kök dosyalarını klasör yapısını koruyarak repoya commit edin.
   Workflow dosyasının repo kökünde `.github/workflows/release.yml` konumunda
   bulunduğunu kontrol edin; yalnızca kod commit etmek otomatik EXE derlemesi kurmaz.
2. GitHub'da **Releases → Draft a new release** açın. Şu anki proje sürümü için
   yeni etiket `v0.2.0` olmalı. Hedef olarak workflow'u içeren güncel commit'i seçin.
3. Deneme sürümünü **Pre-release** olarak işaretleyip **Publish release** düğmesine
   basın. Actions'ta **Build and release Windows ZIP** çalıştırmasının başarılı
   olmasını bekleyin.
4. Arkadaşınız Assets içindeki `SteamCardCollector-0.2.0-win-x64.zip` dosyasını
   indirmeli. Otomatik `Source code (zip)` bağlantısı hazır uygulama içermez.

Kişisel API key veya PAT eklemek gerekmez. Yalnızca Release'e dosya yükleyen iş,
GitHub'ın çalışma süresi için verdiği `GITHUB_TOKEN` ile `contents: write` yetkisi
kullanır; bu token uygulamaya veya ZIP'lere eklenmez.

Önceden oluşturulmuş bir sürümü tekrar paketlemek için **Actions → Build and
release Windows ZIP → Run workflow** açılıp `release_tag` alanına örneğin
`v0.2.0` girilebilir. Bu yol mevcut etiketin kodunu derler; o etikette paket betiği
ve doğru klasör yapısı bulunmalı. Alan boş bırakılırsa seçilen dal yalnızca
derlenir ve dosyalar Actions artifact'i olur; Release'e yüklenmez.

Sonraki sürümde önce `src/SteamCardCollector.App/SteamCardCollector.App.csproj`
içindeki `<Version>` değerini örneğin `0.2.1` yapıp commit edin, ardından `v0.2.1`
etiketiyle yeni Release yayımlayın. Etiket ve proje sürümü farklıysa workflow
yanlış sürümlü paket yayımlamamak için hata verir. Tekrar çalıştırma aynı adlı
Release dosyalarını yenileriyle değiştirir. Hedef reponun Actions politikası
workflow ve gerekli yazma iznine izin vermelidir.

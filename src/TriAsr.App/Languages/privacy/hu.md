# A Mockingbird adatvédelmi nyilatkozata

Hatálybalépés dátuma: 2026. október 1.

## Helyi feldolgozás
A Mockingbird Studio, a Mockingbird Server és a Mockingbird Client ugyanazt a nyilatkozatot használja; az „alkalmazás” az Ön által telepítettet jelenti. A Studio és a Server az Ön számítógépén írja át a felvételeket és javítja az átiratokat. A Client maga semmit sem ír át: az Ön által kiválasztott felvételek ahhoz a szerverhez kerülnek, amelyhez csatlakozik, az átírja őket, az átiratok pedig visszaérkeznek; máshová nem mennek. Az alkalmazás nem tölt fel médiafájlokat vagy átiratokat semmilyen átírási szolgáltatásba. Nincs beépített analitikája, fiókrendszere, hirdetése vagy automatikus összeomlásjelentés-küldése.

## A számítógépen tárolt fájlok
A projektek tartalmazhatják a forrásmédia elérési útját és hash-értékét, a normalizált hanganyagot, a motorok nyers kimenetét, az átiratokat, az átnézés során Ön által végzett módosításokat, az ellenőrzőpontokat, a hardvermérések eredményeit és az alkalmazásnaplókat. A modellek, a részleges letöltések, az ellenőrzések eredményét rögzítő fájlok és a beállítások helyben kerülnek mentésre. Ezek a fájlok a felvételekből vagy a fájlok elérési útjából származó személyes adatokat is tartalmazhatnak. Az alkalmazás nem titkosítja őket.

A telepítőcsomaggal terjesztett verziók a korábbi LocalAppData/TriASR könyvtárat használják, hacsak nem választ más projekt- vagy modellmappát. Ez megőrzi a meglévő telepítéseket. Az alkalmazás eltávolítása a projekteket, a beállításokat és a letöltött modelleket a helyükön hagyja. Adatai eltávolításához zárja be az alkalmazást, és törölje saját kezűleg a kiválasztott mappákat. A megtartani kívánt felvételeket és exportokat előtte mentse el. A Server a fájljait a LocalAppData/TriASR-Server, a Client a LocalAppData/TriASR-Client mappában tárolja.

## Hálózati kapcsolatok
Amikor modell- vagy futtatókörnyezet-letöltést kér, az alkalmazás a Hugging Face vagy a GitHub szolgáltatásához és azok letöltési infrastruktúrájához csatlakozik. Ezek a szolgáltatók a szokásos kapcsolati adatokat kapják meg, például az Ön IP-címét és a kért fájlt. A média- és átirattartalmak nem részei ezeknek a letöltési kéréseknek. A szolgáltatók saját szolgáltatásaira a saját adatvédelmi szabályzatuk vonatkozik:
- https://huggingface.co/privacy
- https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement

### Szerverek és a hálózat
Alapértelmezés szerint ki van kapcsolva (az újonnan telepített Server kiadás bekapcsolja, mert a szolgálás a célja). Amíg egy szerver be van kapcsolva, az alkalmazás figyel a számítógép egy portján, hogy más számítógépek és programok felvételeket küldhessenek neki, és lekérhessék az átiratokat. Az „Elérhető a hálózat más számítógépeiről” beállítás nélkül csak az ezen a számítógépen futó programok csatlakozhatnak. Vele a szerver bejelenti a nevét, a címét és a tanúsítványa ujjlenyomatát a helyi hálózaton (UDP-szórás kétmásodpercenként), és a hálózaton minden kapcsolat titkosított egy tanúsítvánnyal, amelyet az alkalmazás maga készít; az először csatlakozó számítógép megmutatja a felhasználójának az ujjlenyomatot az összehasonlításhoz. A szerver jelszóval védhető; jelszó nélkül bárki használhatja, aki eléri. Az a jelszó, amelyet egy kliensnek megjegyeztet, a Windows-fiókja által védetten tárolódik; a szerver jelszava a beállításfájljában van, titkosítatlanul, mint a többi fájl. A szervernek küldött felvételek a projektek mappájába kerülnek, mint bármelyik másik projekt, és elolvashatja őket, aki a szervert üzemelteti. Az alkalmazás ebből semmit sem jelent senki másnak.

### Frissítések keresése
Hacsak ki nem kapcsolja a Beállításokban, az alkalmazás indításkor, legfeljebb 12 óránként, valamint a Frissítések keresése most lehetőség kiválasztásakor egy kis verziófájlt (latest.json) kér le a GitHubról. A GitHub a szokásos kapcsolati adatokat kapja meg, például az Ön IP-címét, a kérés pedig az alkalmazás nevét és verzióját tartalmazza. Felvételek, átiratok, projektadatok, hardveradatok vagy azonosítók nem kerülnek elküldésre. Az alkalmazás soha nem telepít frissítést magától: Ön választja a Frissítés most lehetőséget, és az alkalmazás a letöltött telepítő SHA256-ellenőrzőösszegét futtatás előtt összeveti a közzétett értékkel.

A helyi javítókiszolgáló a loopback-csatolón (127.0.0.1) keresztül kommunikál; ez nem felhőalapú következtetés. Az alkalmazás telepítőjének letöltése szintén a GitHubhoz csatlakozik. A szükséges modellek és futtatókörnyezetek telepítése után az átírás offline is működhet.

## Terminál, exportálás és támogatás
A PowerShell panelen megadott parancsok az Ön Windows-felhasználójának jogosultságaival futnak. A futtatott parancsoktól függően hozzáférhetnek fájlokhoz, kapcsolatba léphetnek hálózati szolgáltatásokkal vagy adatokat továbbíthatnak. A jelen nyilatkozat helyi feldolgozásról szóló kijelentése az alkalmazás átírási funkcióira vonatkozik, nem tetszőleges terminálparancsokra.

Az alkalmazás nem küld el automatikusan naplókat. Mielőtt GitHub-hibajegyekben megosztaná őket, nézze át a diagnosztikát, a motorok kimenetét és az átiratokat. Az adatok másolását vagy exportálását Ön kezdeményezi. Az operációs rendszer, a biztonsági mentések, a szinkronizált mappák, a biztonsági szoftverek és a külső futtatókörnyezetek a saját szabályzataik szerint kezelhetik a fájlokat.

## Változások és kérdések
A jövőbeli kiadások módosíthatják ezt a nyilatkozatot. Mindig a telepített verzióhoz mellékelt nyilatkozatot tekintse át. Adatvédelmi kérdéseit a projekt GitHub-hibajegyein keresztül teheti fel, személyes felvételek, átiratok vagy naplók csatolása nélkül.

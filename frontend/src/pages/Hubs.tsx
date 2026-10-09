// Las pantallas del menú. Cada una junta, como tabs, páginas que antes eran items
// sueltos del navbar (45 rutas → 7 pantallas). Las rutas viejas redirigen acá (App.tsx).
import TabbedPage from '../components/TabbedPage';
import WaCoverageCard from '../components/WaCoverageCard';
import { isAdmin, useAuthStore } from '../lib/auth';
import Crm from './Crm';
import MyLeads from './MyLeads';
import MapPage from './Map';
import SearchLeads from './SearchLeads';
import Pipeline from './Pipeline';
import LeadsImport from './LeadsImport';
import Remarketing from './Remarketing';
import Devices from './Devices';
import Connect from './Connect';
import Transcripcion from './Transcripcion';
import VoiceTest from './VoiceTest';
import Products from './Products';
import Diccionario from './Diccionario';
import ReglasIa from './ReglasIa';
import Soporte from './Soporte';
import OnboardingApps from './OnboardingApps';
import ProbarBot from './ProbarBot';
import Simulacion from './Simulacion';
import Seguimientos from './Seguimientos';
import Pitches from './Pitches';
import Mensajeria from './Mensajeria';
import Sellers from './Sellers';
import SellerZones from './SellerZones';
import Objetivos from './Objetivos';
import Posteos from './Posteos';
import CalendarPosteos from './CalendarPosteos';
import WarmrQueue from './WarmrQueue';
import Seo from './Seo';
import InstagramAccounts from './InstagramAccounts';
import InstagramFollow from './InstagramFollow';
import Inspiracion from './Inspiracion';
import Competitors from './Competitors';
import Trends from './Trends';

/** CRM de cold calling: pipeline, lista, mapa, captación y remarketing. */
export function CrmHub() {
  const admin = isAdmin(useAuthStore((s) => s.user));
  return (
    <TabbedPage
      tabs={[
        { key: 'pipeline', label: 'Pipeline', element: <Crm /> },
        { key: 'lista', label: admin ? 'Lista' : 'Mis leads', element: <MyLeads /> },
        { key: 'mapa', label: 'Mapa', element: <MapPage />, hidden: !admin },
        {
          key: 'captar', label: 'Captar leads', element: (
            <TabbedPage param="sub" tabs={[
              { key: 'maps', label: 'Buscar en Maps', element: <SearchLeads /> },
              { key: 'scrapers', label: 'Scrapers', element: <Pipeline />, hidden: !admin },
              { key: 'importar', label: 'Importar', element: <LeadsImport /> },
            ]} />
          ),
        },
        { key: 'remarketing', label: 'Remarketing', element: <Remarketing />, hidden: !admin },
      ]}
    />
  );
}

/** Todo lo que es conectar y gestionar líneas de WhatsApp y celulares. */
export function DevicesHub() {
  return (
    <TabbedPage
      tabs={[
        {
          key: 'lineas', label: 'Líneas y celulares', element: (
            <div className="space-y-6">
              <section className="space-y-2">
                <h2 className="text-xl font-bold">Líneas por app</h2>
                <p className="text-sm text-slate-500">Cada app con su línea propia o su vendedor dueño. QR al toque si falta.</p>
                <WaCoverageCard />
              </section>
              <Devices />
            </div>
          ),
        },
        { key: 'mi-linea', label: 'Mi WhatsApp', element: <Connect /> },
        { key: 'transcripcion', label: 'Transcripción', element: <Transcripcion /> },
        { key: 'voz', label: 'Nota de voz (prueba)', element: <VoiceTest /> },
      ]}
    />
  );
}

/** Gestión de las apps: datos y cadencias, el bot y las automatizaciones. */
export function AppsHub() {
  return (
    <TabbedPage
      tabs={[
        { key: 'general', label: 'Apps y cadencias', element: <Products /> },
        {
          key: 'bot', label: 'Bot', element: (
            <TabbedPage param="sub" tabs={[
              { key: 'diccionario', label: 'Diccionario', element: <Diccionario /> },
              { key: 'reglas', label: 'Reglas IA', element: <ReglasIa /> },
              { key: 'soporte', label: 'Soporte', element: <Soporte /> },
              { key: 'onboarding', label: 'Onboarding', element: <OnboardingApps /> },
              { key: 'probar', label: 'Probar', element: <ProbarBot /> },
              { key: 'simulacion', label: 'Simulación', element: <Simulacion /> },
            ]} />
          ),
        },
        {
          key: 'auto', label: 'Automatizaciones', element: (
            <TabbedPage param="sub" tabs={[
              { key: 'seguimientos', label: 'Seguimientos', element: <Seguimientos /> },
              { key: 'pitches', label: 'Pitches por anuncio', element: <Pitches /> },
              { key: 'mensajeria', label: 'Política de mensajes', element: <Mensajeria /> },
            ]} />
          ),
        },
      ]}
    />
  );
}

/** Vendedores, sus zonas y sus objetivos. */
export function TeamHub() {
  return (
    <TabbedPage
      tabs={[
        { key: 'vendedores', label: 'Vendedores', element: <Sellers /> },
        { key: 'zonas', label: 'Zonas', element: <SellerZones /> },
        { key: 'objetivos', label: 'Objetivos', element: <Objetivos /> },
      ]}
    />
  );
}

/** Contenido, SEO, Instagram y competencia. */
export function MarketingHub() {
  return (
    <TabbedPage
      tabs={[
        {
          key: 'posteos', label: 'Posteos', element: (
            <TabbedPage param="sub" tabs={[
              { key: 'lista', label: 'Posteos', element: <Posteos /> },
              { key: 'calendario', label: 'Calendario', element: <CalendarPosteos /> },
              { key: 'warmr', label: 'Cola Warmr', element: <WarmrQueue /> },
            ]} />
          ),
        },
        { key: 'seo', label: 'SEO', element: <Seo /> },
        {
          key: 'instagram', label: 'Instagram', element: (
            <TabbedPage param="sub" tabs={[
              { key: 'cuentas', label: 'Cuentas', element: <InstagramAccounts /> },
              { key: 'follow', label: 'Auto-follow', element: <InstagramFollow /> },
            ]} />
          ),
        },
        {
          key: 'competencia', label: 'Competencia', element: (
            <TabbedPage param="sub" tabs={[
              { key: 'inspiracion', label: 'Inspiración', element: <Inspiracion /> },
              { key: 'competidores', label: 'Competidores', element: <Competitors /> },
              { key: 'tendencias', label: 'Tendencias', element: <Trends /> },
            ]} />
          ),
        },
      ]}
    />
  );
}

import type { ComponentType } from 'react';
import { Link } from 'react-router';
import HeroIllustration from '../components/illustrations/HeroIllustration';
import {
  BookIllustration,
  BranchIllustration,
  DeliveredIllustration,
  OutForDeliveryIllustration,
  TransitIllustration,
} from '../components/illustrations/StepIllustrations';

interface JourneyStep {
  status: string;
  title: string;
  body: string;
  Illustration: ComponentType<{ title: string; className?: string }>;
}

// The stages customers will see when tracking a parcel. Staff move a parcel from one stage to the next.
const journey: JourneyStep[] = [
  {
    status: 'Booking created',
    title: 'Book your parcel',
    body: 'Book on our website, or visit any branch and our staff will book it for you.',
    Illustration: BookIllustration,
  },
  {
    status: 'Picked up',
    title: 'Hand it over',
    body: 'We collect it or receive it at a branch, weigh it and confirm the price in NPR, then ship it.',
    Illustration: BranchIllustration,
  },
  {
    status: 'In transit',
    title: 'On the way',
    body: 'Your parcel travels by truck through our hubs and warehouses to the destination city.',
    Illustration: TransitIllustration,
  },
  {
    status: 'Out for delivery',
    title: 'Rider heads out',
    body: 'A local rider brings the parcel to the address: municipality, ward and tole.',
    Illustration: OutForDeliveryIllustration,
  },
  {
    status: 'Delivered',
    title: 'Delivered',
    body: 'The recipient gets the parcel, and the tracking page shows it as delivered.',
    Illustration: DeliveredIllustration,
  },
];

const features = [
  {
    title: 'Book online or at the counter',
    body: 'Customers book from the website; branch staff create bookings for walk-in customers.',
  },
  {
    title: 'Track with one number',
    body: 'Every parcel gets a unique tracking number. Staff update its status at each step of the journey.',
  },
  {
    title: 'Built for Nepal',
    body: 'Province → district → municipality → ward addresses, Nepali phone numbers and NPR pricing.',
  },
];

export default function HomePage() {
  return (
    <div className="space-y-14">
      {/* Hero */}
      <section className="grid items-center gap-8 overflow-hidden rounded-2xl bg-gradient-to-br from-brand-700 to-accent-700 px-6 py-10 text-white sm:px-10 lg:grid-cols-2">
        <div>
          <h1 className="text-3xl font-bold tracking-tight sm:text-4xl">
            Parcels delivered across all seven provinces of Nepal
          </h1>
          <p className="mt-4 max-w-xl text-white/85">
            From Mechi to Mahakali, send documents and packages. See the price in NPR before you confirm your booking.
          </p>
          <div className="mt-8 flex flex-wrap gap-3">
            <Link
              to="/track"
              className="rounded-lg bg-white px-5 py-2.5 text-sm font-semibold text-brand-700 shadow hover:bg-brand-50"
            >
              Track a parcel
            </Link>
            <Link
              to="/book"
              className="rounded-lg border border-white/60 px-5 py-2.5 text-sm font-semibold text-white hover:bg-white/10"
            >
              Book a parcel
            </Link>
          </div>
        </div>
        <HeroIllustration className="w-full drop-shadow-xl" />
      </section>

      {/* How delivery works */}
      <section aria-labelledby="how-it-works">
        <div className="text-center">
          <h2 id="how-it-works" className="text-2xl font-bold tracking-tight">
            How your parcel reaches its destination
          </h2>
          <p className="mx-auto mt-2 max-w-2xl text-sm text-slate-600">
            Five simple steps. The highlighted label on each card is the status you will see when you track your parcel.
          </p>
        </div>

        <ol className="mt-8 grid gap-4 sm:grid-cols-2 lg:grid-cols-5">
          {journey.map(({ status, title, body, Illustration }, index) => (
            <li
              key={status}
              className="relative flex flex-col items-center rounded-xl border border-slate-200 bg-white p-5 text-center shadow-sm"
            >
              <span className="absolute left-3 top-3 flex h-7 w-7 items-center justify-center rounded-full bg-brand-600 text-sm font-bold text-white">
                {index + 1}
              </span>
              <Illustration title={title} className="h-28 w-28" />
              <span className="mt-3 rounded-full bg-brand-50 px-2.5 py-0.5 text-xs font-semibold text-brand-700">
                {status}
              </span>
              <h3 className="mt-2 font-semibold text-slate-900">{title}</h3>
              <p className="mt-1 text-sm text-slate-600">{body}</p>
              {index < journey.length - 1 && (
                <span
                  aria-hidden
                  className="absolute -right-3 top-1/2 z-10 hidden h-6 w-6 -translate-y-1/2 items-center justify-center rounded-full bg-white text-brand-600 shadow ring-1 ring-slate-200 lg:flex"
                >
                  →
                </span>
              )}
            </li>
          ))}
        </ol>
      </section>

      {/* Features */}
      <section className="grid gap-4 sm:grid-cols-3">
        {features.map((f) => (
          <article key={f.title} className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm">
            <h2 className="font-semibold text-slate-900">{f.title}</h2>
            <p className="mt-2 text-sm text-slate-600">{f.body}</p>
          </article>
        ))}
      </section>
    </div>
  );
}

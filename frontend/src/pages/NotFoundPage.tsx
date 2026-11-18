import { Link } from 'react-router';

export default function NotFoundPage() {
  return (
    <div className="py-16 text-center">
      <p className="text-sm font-semibold text-brand-600">404</p>
      <h1 className="mt-2 text-2xl font-bold">Page not found</h1>
      <Link to="/" className="mt-6 inline-block text-sm font-medium text-brand-700 underline">
        Back to home
      </Link>
    </div>
  );
}

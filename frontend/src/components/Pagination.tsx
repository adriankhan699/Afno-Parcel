import { SecondaryButton } from './form';

interface PaginationProps {
  page: number;
  totalPages: number;
  totalCount: number;
  hasNextPage: boolean;
  /** "parcel", "branch"… used in "12 parcels". */
  noun: string;
  onPage: (page: number) => void;
}

export default function Pagination({ page, totalPages, totalCount, hasNextPage, noun, onPage }: PaginationProps) {
  return (
    <nav aria-label="Pagination" className="mt-4 flex items-center justify-between gap-3 text-sm">
      <p className="text-slate-600">
        Page {page} of {Math.max(totalPages, 1)} · {totalCount} {noun}
        {totalCount === 1 ? '' : 's'}
      </p>
      <div className="flex gap-2">
        <SecondaryButton type="button" disabled={page <= 1} onClick={() => onPage(Math.max(1, page - 1))}>
          Previous
        </SecondaryButton>
        <SecondaryButton type="button" disabled={!hasNextPage} onClick={() => onPage(page + 1)}>
          Next
        </SecondaryButton>
      </div>
    </nav>
  );
}
